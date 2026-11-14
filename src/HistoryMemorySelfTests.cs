using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

internal static class HistoryMemorySelfTests
{
    internal static IEnumerable<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexHistoryMemoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = DateTime.Today.AddHours(12).ToUniversalTime();
            DateTime today = now.ToLocalTime().Date;
            var rows = Enumerable.Range(0, 256).Select(i => Flow(today.AddHours(1), i % 2 == 0 ? TrackedApplication.Codex : TrackedApplication.Claude,
                "target-" + i + ".test", i % 3 == 0 ? "Vless" : i % 3 == 1 ? "Direct" : "Unknown", i + 1)).ToList();
            string session = WriteSession(root, "active", rows);
            var history = new HistoryAnalytics(root, () => now);
            history.Refresh();
            object originalPublication = history.PublishedVersion;
            var originalTargets = PublishedTargets(originalPublication).ToArray();
            if (originalTargets.Distinct(ReferenceEqualityComparer.Instance).Count() != rows.Count)
                throw new InvalidOperationException("Equivalent calendar and proxy ranges retained duplicate target entities.");
            yield return "History retains one immutable target entity across equivalent calendar and proxy ranges";

            foreach (var range in Enum.GetValues<DashboardRange>())
            foreach (var scope in Enum.GetValues<DashboardTrafficScope>())
            foreach (var application in Enum.GetValues<DashboardApplicationScope>())
            {
                var expected = rows.Where(r => ApplicationFilter.Matches(r.Application, application) &&
                    (scope == DashboardTrafficScope.All || r.ProxyType == "Vless")).ToList();
                var actual = history.GetSnapshot(range, scope, application);
                if (actual.UploadBytes != expected.Sum(r => r.UploadBytes) || actual.DownloadBytes != expected.Sum(r => r.DownloadBytes) ||
                    actual.Targets.Count != expected.Count || actual.Targets.Sum(t => t.UploadBytes) != actual.UploadBytes)
                    throw new InvalidOperationException($"Shared history changed {range}/{scope}/{application} accounting or targets.");
                if (actual.Targets.Count > 0) { actual.Targets[0].UploadBytes = long.MaxValue; actual.Targets[0].ProxyType = "mutated"; }
                actual.Targets.Clear();
                actual.DailyUsage[0].UploadBytes = long.MaxValue;
                if (history.GetSnapshot(range, scope, application).UploadBytes != expected.Sum(r => r.UploadBytes))
                    throw new InvalidOperationException("Caller mutations escaped their history snapshot.");
            }
            var exported = history.GetSnapshot(DashboardRange.All);
            string csv = Path.Combine(root, "all-targets.csv");
            SessionStore.WriteCsv(csv, exported.Targets);
            if (File.ReadLines(csv).Count() != rows.Count + 1 || !File.ReadAllText(csv).Contains("target-255.test"))
                throw new InvalidOperationException("Shared history lost full CSV export targets.");
            yield return "All 24 history selectors preserve complete directional counters, mutable isolation and full CSV export";

            long[] originalBytes = originalTargets.Select(t => t.UploadBytes).ToArray();
            var appended = Flow(today.AddHours(2), TrackedApplication.Codex, "target-0.test", "Vless", 17);
            appended.Route = "changed route"; appended.Region = "日本"; appended.RegionSource = "test";
            Append(session, appended); rows.Add(appended); WriteSummary(session, rows);
            history.Refresh();
            if (!originalTargets.Select(t => t.UploadBytes).SequenceEqual(originalBytes) || originalTargets.Any(t => t.Route == "多种路径"))
                throw new InvalidOperationException("Appending to a daily target mutated a previously published history version.");
            var afterAppend = history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.Proxy, DashboardApplicationScope.Codex);
            var changed = afterAppend.Targets.Single(t => t.Hostname == "target-0.test");
            if (changed.UploadBytes != 18 || changed.Region != "日本" || changed.Route != "多种路径")
                throw new InvalidOperationException("Copy-on-write lost appended bytes or mutable target metadata.");
            yield return "Appending to shared daily targets preserves previous publications and updates bytes, route and region";

            var yesterday = Flow(today.AddDays(-1).AddHours(1), TrackedApplication.Codex, "target-0.test", "Vless", 31);
            yesterday.Region = "美国"; yesterday.RegionSource = "test";
            string previous = WriteSession(root, "previous", new List<FlowRecord> { yesterday });
            history.Refresh();
            var todayTarget = history.GetSnapshot(DashboardRange.Today).Targets.Single(t => t.Hostname == "target-0.test");
            var weekTarget = history.GetSnapshot(DashboardRange.Last7Days).Targets.Single(t => t.Hostname == "target-0.test");
            if (todayTarget.UploadBytes != 18 || todayTarget.Region != "日本" || weekTarget.UploadBytes != 49 || weekTarget.Region != "多个地区")
                throw new InvalidOperationException("Range sharing confused per-day targets with merged multi-session aggregates.");
            if (PublishedTargets(history.PublishedVersion).Where(t => t.Hostname == "target-0.test")
                .Distinct(ReferenceEqualityComparer.Instance).Count() != 2)
                throw new InvalidOperationException("Equivalent multi-session aggregates were not shared across ranges.");
            yield return "Different calendar aggregates retain separate metadata while equal multi-session ranges share their result";

            object beforeRewrite = history.PublishedVersion;
            long[] beforeRewriteBytes = PublishedTargets(beforeRewrite).Select(t => t.UploadBytes).ToArray();
            var replacement = Flow(today.AddHours(3), TrackedApplication.Claude, "replacement.test", "Direct", 3);
            rows = new List<FlowRecord> { replacement };
            File.WriteAllText(Path.Combine(session, "flows.jsonl"), JsonSerializer.Serialize(replacement) + "\n", new UTF8Encoding(false));
            WriteSummary(session, rows);
            history.Refresh();
            if (history.GetSnapshot(DashboardRange.All).UploadBytes != 34 || history.GetSnapshot(DashboardRange.Today).Targets.Single().Hostname != "replacement.test" ||
                !PublishedTargets(beforeRewrite).Select(t => t.UploadBytes).SequenceEqual(beforeRewriteBytes))
                throw new InvalidOperationException("A replaced flow file invalidated old shared targets or retained removed targets.");
            Directory.Delete(previous, recursive: true);
            history.Refresh();
            if (history.GetSnapshot(DashboardRange.All).UploadBytes != 3 || history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.Proxy).Targets.Count != 0)
                throw new InvalidOperationException("Deleted sessions retained shared historical targets.");
            now = now.AddDays(1); history.Refresh();
            if (history.GetSnapshot(DashboardRange.Today).Targets.Count != 0 || history.GetSnapshot(DashboardRange.All).UploadBytes != 3)
                throw new InvalidOperationException("Midnight retained shared targets in today's range.");
            yield return "Flow replacement, session deletion and local midnight correctly retire shared history targets";

            // Hold the indexing gate to model a slow disk refresh. A public query
            // must still finish from the last immutable publication.
            object gate = typeof(HistoryAnalytics).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task worker = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } });
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("History locking fixture did not start.");
            try
            {
                Task<DashboardSnapshot> query = Task.Run(() => history.GetSnapshot(DashboardRange.All));
                if (!query.Wait(TimeSpan.FromSeconds(2)) || query.Result.UploadBytes != 3)
                    throw new InvalidOperationException("History UI query waited for the file-indexing lock.");
            }
            finally { release.Set(); worker.GetAwaiter().GetResult(); }
            yield return "Shared history snapshots remain responsive while another thread owns the file-indexing lock";
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static FlowRecord Flow(DateTime local, TrackedApplication application, string host, string type, long upload) => new()
    {
        Application = application, TimestampUtc = local.ToUniversalTime(), Process = "node.exe", Protocol = "TCP",
        RemoteAddress = "127.0.0.1", RemotePort = 7897, ProxyReportedHost = host, ProxyReportedPort = 443,
        ProxyType = type, ProxyNode = "Shared", ProxyReportedChain = "Auto → Shared", Route = "original route",
        UploadBytes = upload, DownloadBytes = upload * 2
    };

    private static string WriteSession(string root, string id, List<FlowRecord> rows)
    {
        string directory = Path.Combine(root, "sessions", id); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "flows.jsonl"), string.Concat(rows.Select(r => JsonSerializer.Serialize(r) + "\n")), new UTF8Encoding(false));
        WriteSummary(directory, rows);
        return directory;
    }

    private static void WriteSummary(string directory, List<FlowRecord> rows) => File.WriteAllText(Path.Combine(directory, "session.json"),
        JsonSerializer.Serialize(new SessionRecord
        {
            Id = Path.GetFileName(directory), StartedUtc = rows.Min(r => r.TimestampUtc), UpdatedUtc = rows.Max(r => r.TimestampUtc),
            AccountingVersion = 2, UploadBytes = rows.Sum(r => r.UploadBytes), DownloadBytes = rows.Sum(r => r.DownloadBytes),
            CodexUploadBytes = rows.Where(r => r.Application == TrackedApplication.Codex).Sum(r => r.UploadBytes),
            CodexDownloadBytes = rows.Where(r => r.Application == TrackedApplication.Codex).Sum(r => r.DownloadBytes),
            ClaudeUploadBytes = rows.Where(r => r.Application == TrackedApplication.Claude).Sum(r => r.UploadBytes),
            ClaudeDownloadBytes = rows.Where(r => r.Application == TrackedApplication.Claude).Sum(r => r.DownloadBytes)
        }));

    private static void Append(string directory, FlowRecord row)
    {
        using var stream = new FileStream(Path.Combine(directory, "flows.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n"); stream.Write(bytes); stream.Flush(true);
    }

    private static IEnumerable<TargetRow> PublishedTargets(object publication)
    {
        foreach (DictionaryEntry entry in (IDictionary)publication)
        {
            var snapshot = (DashboardSnapshot)entry.Value!.GetType().GetProperty("Total")!.GetValue(entry.Value)!;
            foreach (var row in snapshot.Targets) yield return row;
        }
    }
}
