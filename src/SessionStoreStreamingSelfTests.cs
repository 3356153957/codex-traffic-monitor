using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

internal static class SessionStoreStreamingSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        string directory = Path.Combine(Path.GetTempPath(), "CodexTrafficStreamingTests", Guid.NewGuid().ToString("N"));
        var timestamp = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 500).Select(i => new FlowRecord
        {
            TimestampUtc = timestamp.AddSeconds(i), LastTimestampUtc = timestamp.AddSeconds(i).AddMilliseconds(500),
            Application = i % 2 == 0 ? TrackedApplication.Codex : TrackedApplication.Claude,
            Process = i % 2 == 0 ? "codex.exe" : "claude.exe", ProcessPath = "C:\\工具\\app.exe",
            Protocol = i % 3 == 0 ? "UDP6" : "TCP", LocalAddress = "::1", LocalPort = 10000 + i,
            RemoteAddress = "203.0.113.1", RemotePort = 443, ProxyReportedHost = "target-" + i + ".example",
            ProxyReportedChain = "策略组 → 节点\n含\"引号\"", UploadBytes = i + 1, DownloadBytes = 2 * i + 1
        }).ToArray();
        using var store = new SessionStore(directory, true, true);
        string ReadLog()
        {
            using var input = new FileStream(store.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var text = new StreamReader(input, Encoding.UTF8);
            return text.ReadToEnd();
        }
        string expected = string.Join("\n", rows.Select(row => JsonSerializer.Serialize(row))) + "\n";
        store.Append(rows);
        Check(ReadLog() == expected, "Streaming JSONL preserves every byte of the prior format, Unicode, escaping and row order");
        store.Append(Array.Empty<FlowRecord>());
        Check(ReadLog() == expected, "An empty append leaves durable JSONL unchanged");
        IEnumerable<FlowRecord> FailPartway()
        {
            yield return rows[0];
            yield return rows[1];
            throw new IOException("Injected iterator failure after a written prefix");
        }
        bool failed = false;
        try { store.Append(FailPartway()); }
        catch (IOException) { failed = true; }
        Check(failed, "A mid-batch iterator failure is reported to the caller");
        Check(ReadLog() == expected, "A failed streaming batch rolls back its entire prefix and preserves earlier rows");
        store.Append(rows.Take(2));
        string retried = expected + string.Join("\n", rows.Take(2).Select(row => JsonSerializer.Serialize(row))) + "\n";
        Check(ReadLog() == retried, "A retry after rollback appends exactly once at the original checkpoint");
        var restored = ReadLog().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => JsonSerializer.Deserialize<FlowRecord>(row)!).ToList();
        Check(restored.Count == 502 && restored.Sum(row => row.UploadBytes) == rows.Sum(row => row.UploadBytes) + 3 &&
            restored.Sum(row => row.DownloadBytes) == rows.Sum(row => row.DownloadBytes) + 4,
            "Streaming and retry retain all directional byte counters and record boundaries");
        return checks;
    }
}
