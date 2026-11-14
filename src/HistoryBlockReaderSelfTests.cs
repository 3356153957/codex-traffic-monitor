using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodexTraffic;

internal static class HistoryBlockReaderSelfTests
{
    internal static IEnumerable<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexHistoryBlockTests", Guid.NewGuid().ToString("N"));
        DateTime now = DateTime.Today.AddHours(12).ToUniversalTime();
        int revision = 0;
        var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var row = new FlowRecord { TimestampUtc = now, Process = "codex.exe", Protocol = "TCP",
            RemoteAddress = "203.0.113.1", RemotePort = 443, ProxyReportedHost = "中文测试.example", UploadBytes = 7, DownloadBytes = 11 };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(row, json);
        string Session(string name, byte[] content, bool closed, long up = 7, long down = 11)
        {
            string data = Path.Combine(root, name), session = Path.Combine(data, "sessions", "one");
            Directory.CreateDirectory(session);
            File.WriteAllBytes(Path.Combine(session, "flows.jsonl"), content);
            Summary(session, closed, up, down);
            return data;
        }
        void Summary(string session, bool closed, long up, long down)
        {
            string path = Path.Combine(session, "session.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new SessionRecord { Id = "one", StartedUtc = now,
                UpdatedUtc = now, EndedUtc = closed ? now : null, Status = closed ? "completed" : "recording",
                AccountingVersion = 2, UploadBytes = up, DownloadBytes = down, CodexUploadBytes = up, CodexDownloadBytes = down }));
            File.SetLastWriteTimeUtc(path, now.AddSeconds(++revision));
        }
        void Check(HistoryAnalytics history, long up, long down, bool warning = false)
        {
            var snapshot = history.GetSnapshot(DashboardRange.All);
            if (snapshot.Targets.Sum(t => t.UploadBytes) != up || snapshot.Targets.Sum(t => t.DownloadBytes) != down ||
                (snapshot.Warning.Length > 0) != warning)
                throw new InvalidOperationException($"Block reader mismatch: expected {up}/{down}, got {snapshot.Targets.Sum(t => t.UploadBytes)}/{snapshot.Targets.Sum(t => t.DownloadBytes)}, warning={snapshot.Warning}");
        }
        byte[] Padded(int size, bool newline)
        {
            byte[] content = new byte[size + (newline ? 1 : 0)];
            content.AsSpan(0, size - bytes.Length).Fill((byte)' ');
            bytes.CopyTo(content, size - bytes.Length);
            if (newline) content[^1] = (byte)'\n';
            return content;
        }
        try
        {
            // Put either LF or a multibyte UTF-8 character across the 64 KiB read boundary.
            int unicode = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("中"));
            foreach (int size in new[] { 65535, 65536, 65537, 131073, 65536 - unicode + bytes.Length - 1 })
            {
                string data = Session("boundary-" + size, Padded(size, true), true);
                var history = new HistoryAnalytics(data, () => now); history.Refresh(); Check(history, 7, 11);
                if (history.GetSnapshot(DashboardRange.All).Targets.Single().Hostname != row.ProxyReportedHost)
                    throw new InvalidOperationException("Split UTF-8 changed the hostname");
            }
            yield return "History block scanning preserves LF and multibyte UTF-8 across each 64 KiB boundary";

            byte[] bom = Encoding.UTF8.GetPreamble();
            string endings = Session("endings", bom.Concat(bytes).Concat("\r\n\n\r\n"u8.ToArray()).Concat(bytes).ToArray(), true, 14, 22);
            var endingsHistory = new HistoryAnalytics(endings, () => now); endingsHistory.Refresh(); Check(endingsHistory, 14, 22);
            yield return "BOM, CRLF, blank lines and a valid closed final row without LF preserve exact totals";

            const int maximum = 4 * 1024 * 1024;
            foreach (bool newline in new[] { false, true })
            {
                string data = Session("at-limit-" + newline, Padded(maximum, newline), true);
                var history = new HistoryAnalytics(data, () => now); history.Refresh(); Check(history, 7, 11);
            }
            yield return "A four MiB history row is accepted both with and without a final newline";

            string oversized = Session("over-limit", Padded(maximum + 1, true).Concat(bytes).Append((byte)'\n').ToArray(), true);
            var oversizedHistory = new HistoryAnalytics(oversized, () => now); oversizedHistory.Refresh(); Check(oversizedHistory, 7, 11, true);
            if (!oversizedHistory.GetSnapshot(DashboardRange.All).Warning.Contains("1 条损坏明细"))
                throw new InvalidOperationException("An oversized row was counted more than once");
            yield return "One oversized row is skipped exactly once and the following valid row is still read";

            string tail = Session("oversized-tail", Padded(maximum + 1, false), true, 0, 0);
            var tailHistory = new HistoryAnalytics(tail, () => now); tailHistory.Refresh(); Check(tailHistory, 0, 0, true);
            if (!tailHistory.GetSnapshot(DashboardRange.All).Warning.Contains("末尾记录不完整"))
                throw new InvalidOperationException("Oversized closed tail lost its warning");
            yield return "An oversized unterminated tail remains uncommitted and reports its incomplete state";

            byte[] splitRow = Padded(131073, false);
            int split = splitRow.Length - bytes.Length + unicode + 1;
            string active = Session("active", bytes.Append((byte)'\n').Concat(splitRow[..split]).ToArray(), false);
            string activeSession = Path.Combine(active, "sessions", "one"), log = Path.Combine(activeSession, "flows.jsonl");
            var activeHistory = new HistoryAnalytics(active, () => now); activeHistory.Refresh(); Check(activeHistory, 7, 11);
            activeHistory.Refresh(); Check(activeHistory, 7, 11);
            using (var append = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(splitRow.AsSpan(split));
            activeHistory.Refresh(); Check(activeHistory, 7, 11);
            using (var append = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.WriteByte((byte)'\n');
            Summary(activeSession, false, 14, 22);
            activeHistory.Refresh(); Check(activeHistory, 14, 22);
            activeHistory.Refresh(); Check(activeHistory, 14, 22);
            yield return "An active split UTF-8 row spanning several blocks is committed once only after its newline arrives";

            string errors = Session("errors", "{bad json}\n"u8.ToArray().Concat(bytes).Append((byte)'\n').ToArray(), true);
            var errorsHistory = new HistoryAnalytics(errors, () => now); errorsHistory.Refresh(); Check(errorsHistory, 7, 11, true);
            yield return "Malformed JSON does not prevent the next valid row in the same block from loading";
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
