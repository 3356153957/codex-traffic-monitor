using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

internal static class HistoryReplacementSelfTests
{
    internal static IEnumerable<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexHistoryReplacementTests", Guid.NewGuid().ToString("N"));
        DateTime now = DateTime.Today.AddHours(12).ToUniversalTime();
        int revision = 0;
        try
        {
            foreach (bool closed in new[] { false, true })
            {
                string data = Path.Combine(root, closed ? "closed" : "active");
                string session = Path.Combine(data, "sessions", "fixture");
                Directory.CreateDirectory(session);
                string log = Path.Combine(session, "flows.jsonl");
                void Replace(List<FlowRecord> rows, bool atomic = true)
                {
                    string destination = atomic ? log + ".replacement" : log;
                    File.WriteAllText(destination, string.Concat(rows.Select(r => JsonSerializer.Serialize(r) + "\n")), new UTF8Encoding(false));
                    // Deliberately distinct stamps avoid depending on the host
                    // filesystem's timestamp resolution for replacement tests.
                    File.SetLastWriteTimeUtc(destination, now.AddSeconds(++revision));
                    if (atomic) File.Move(destination, log, true);
                    WriteSummary(session, rows, closed, now, ++revision);
                }

                Replace(new() { Flow(now, "old.test", 900) });
                var history = new HistoryAnalytics(data, () => now);
                history.Refresh();
                object publication = history.PublishedVersion;
                var publishedTargets = PublishedTargets(publication).ToArray();

                Replace(new() { Flow(now, "new.test", 100), Flow(now, "new.test", 100), Flow(now, "new.test", 100) });
                history.Refresh();
                AssertSnapshot(history, 300, "new.test");
                if (publishedTargets.Any(t => t.UploadBytes != 900 || t.Hostname != "old.test"))
                    throw new InvalidOperationException("A longer flow replacement mutated an earlier publication.");

                long longLength = new FileInfo(log).Length;
                Replace(new() { Flow(now, "alt.test", 100), Flow(now, "alt.test", 100), Flow(now, "alt.test", 100) });
                if (new FileInfo(log).Length != longLength) throw new InvalidOperationException("Same-length replacement fixture changed length.");
                history.Refresh();
                AssertSnapshot(history, 300, "alt.test");

                Replace(new() { Flow(now, "one.test", 50) });
                history.Refresh();
                AssertSnapshot(history, 50, "one.test");

                File.Delete(log);
                history.Refresh();
                var missing = history.GetSnapshot(DashboardRange.All);
                if (missing.UploadBytes != 50 || missing.Targets.Count != 0 || !missing.Warning.Contains("无法读取"))
                    throw new InvalidOperationException("A deleted flow file retained stale targets or lost its canonical summary.");
                Replace(new() { Flow(now, "reb.test", 40), Flow(now, "reb.test", 40) });
                history.Refresh();
                AssertSnapshot(history, 80, "reb.test");
                yield return $"{(closed ? "Closed" : "Active")} history rebuilds longer, equal-length, shorter and deleted/recreated files without mutating earlier publications";

                if (closed)
                {
                    // A repair can overwrite an ended log in place; a new
                    // handle identity alone would not catch this longer file.
                    Replace(new() { Flow(now, "fix.test", 10), Flow(now, "fix.test", 10), Flow(now, "fix.test", 10) }, atomic: false);
                    history.Refresh();
                    AssertSnapshot(history, 30, "fix.test");
                    yield return "A changed closed log is rebuilt after a longer in-place repair";
                }
            }

            string appendData = Path.Combine(root, "append");
            string appendSession = Path.Combine(appendData, "sessions", "fixture");
            Directory.CreateDirectory(appendSession);
            string appendLog = Path.Combine(appendSession, "flows.jsonl");
            var first = Flow(now, "append.test", 100);
            var second = Flow(now, "append.test", 200);
            var third = Flow(now, "append.test", 300);
            byte[] secondBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(second));
            int split = secondBytes.Length / 2;
            File.WriteAllText(appendLog, JsonSerializer.Serialize(first) + "\n", new UTF8Encoding(false));
            Append(appendLog, secondBytes.AsSpan(0, split));
            WriteSummary(appendSession, new() { first }, false, now, ++revision);
            var appendHistory = new HistoryAnalytics(appendData, () => now);
            appendHistory.Refresh();
            AssertSnapshot(appendHistory, 100, "append.test");
            object oldDay = FirstDay(appendHistory);
            var beforeAppend = PublishedTargets(appendHistory.PublishedVersion).ToArray();
            Append(appendLog, secondBytes.AsSpan(split));
            Append(appendLog, Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(third) + "\n"));
            WriteSummary(appendSession, new() { first, second, third }, false, now, ++revision);
            appendHistory.Refresh();
            AssertSnapshot(appendHistory, 600, "append.test");
            if (!ReferenceEquals(oldDay, FirstDay(appendHistory)) || beforeAppend.Any(t => t.UploadBytes != 100))
                throw new InvalidOperationException("A normal active append rebuilt the daily index or mutated an old publication.");
            appendHistory.Refresh();
            AssertSnapshot(appendHistory, 600, "append.test");
            yield return "Active append reads from the committed offset and completes a partial line exactly once while retaining the daily index";

            // Ending a session may commit a valid final row without a newline.
            // A later repair adding its terminator must not count that row twice.
            File.WriteAllText(appendLog, JsonSerializer.Serialize(first), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(appendLog, now.AddSeconds(++revision));
            WriteSummary(appendSession, new() { first }, true, now, ++revision);
            appendHistory.Refresh();
            AssertSnapshot(appendHistory, 100, "append.test");
            Append(appendLog, Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(second) + "\n"));
            WriteSummary(appendSession, new() { first, second }, true, now, ++revision);
            appendHistory.Refresh();
            AssertSnapshot(appendHistory, 300, "append.test");
            yield return "Repairing a previously committed final row without a newline does not duplicate its bytes";

            File.WriteAllText(appendLog, JsonSerializer.Serialize(first) + "\n", new UTF8Encoding(false));
            Append(appendLog, secondBytes.AsSpan(0, split));
            WriteSummary(appendSession, new() { first, second }, true, now, ++revision);
            appendHistory.Refresh();
            var incomplete = appendHistory.GetSnapshot(DashboardRange.All);
            if (incomplete.Targets.Single().UploadBytes != 100 || !incomplete.Warning.Contains("末尾记录不完整"))
                throw new InvalidOperationException("A closed partial tail was committed or its warning disappeared.");
            Append(appendLog, secondBytes.AsSpan(split));
            appendHistory.Refresh();
            AssertSnapshot(appendHistory, 300, "append.test");
            yield return "Completing a closed partial tail replaces its warning and commits each row once";
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static FlowRecord Flow(DateTime utc, string host, long bytes) => new()
    {
        TimestampUtc = utc, Process = "codex.exe", Protocol = "TCP", RemoteAddress = "203.0.113.1", RemotePort = 443,
        ProxyReportedHost = host, UploadBytes = bytes, DownloadBytes = bytes * 2
    };

    private static void WriteSummary(string session, List<FlowRecord> rows, bool closed, DateTime now, int revision)
    {
        string path = Path.Combine(session, "session.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new SessionRecord
        {
            Id = "fixture", StartedUtc = now, UpdatedUtc = now, EndedUtc = closed ? now : null,
            Status = closed ? "completed" : "recording", AccountingVersion = 2,
            UploadBytes = rows.Sum(r => r.UploadBytes), DownloadBytes = rows.Sum(r => r.DownloadBytes),
            CodexUploadBytes = rows.Sum(r => r.UploadBytes), CodexDownloadBytes = rows.Sum(r => r.DownloadBytes)
        }));
        File.SetLastWriteTimeUtc(path, now.AddSeconds(revision));
    }

    private static void Append(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static void AssertSnapshot(HistoryAnalytics history, long bytes, string host)
    {
        var snapshot = history.GetSnapshot(DashboardRange.All);
        if (snapshot.UploadBytes != bytes || snapshot.DownloadBytes != bytes * 2 || snapshot.Targets.Count != 1 ||
            snapshot.Targets[0].Hostname != host || snapshot.Targets[0].UploadBytes != bytes || snapshot.Warning.Length != 0)
            throw new InvalidOperationException($"Replacement or append retained stale bytes, targets or warnings: expected {bytes} at {host}; got {snapshot.UploadBytes}, {snapshot.Targets.Count} targets, {snapshot.Warning}.");
    }

    private static object FirstDay(HistoryAnalytics history)
    {
        object sessions = typeof(HistoryAnalytics).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
        object session = ((IDictionary)sessions).Values.Cast<object>().Single();
        object days = session.GetType().GetField("Days")!.GetValue(session)!;
        return ((IDictionary)days).Values.Cast<object>().Single();
    }

    private static IEnumerable<TargetRow> PublishedTargets(object publication)
    {
        foreach (DictionaryEntry entry in (IDictionary)publication)
        {
            var snapshot = (DashboardSnapshot)entry.Value!.GetType().GetProperty("Total")!.GetValue(entry.Value)!;
            foreach (var target in snapshot.Targets) yield return target;
        }
    }
}
