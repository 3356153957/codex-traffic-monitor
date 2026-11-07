using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodexTraffic;

public static class AnalyticsSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "CodexTrafficAnalyticsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DateTime today = DateTime.Today;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name + " (fixtures: " + root + ")");
            checks.Add(name);
        }
        FlowRecord Flow(DateTime local, long upload, long download, string host = "", int pid = 11, int localPort = 51000) => new()
        {
            TimestampUtc = local.ToUniversalTime(), Pid = pid, Process = "codex.exe", Protocol = "TCP", LocalAddress = "127.0.0.1", LocalPort = localPort,
            RemoteAddress = "127.0.0.1", RemotePort = 7897, Route = "本机回环（可能代理）", UploadBytes = upload, DownloadBytes = download,
            ProxyReportedHost = host, ProxyReportedPort = host.Length > 0 ? 443 : 0
        };
        string MakeSession(string data, string id, IEnumerable<FlowRecord> flows, bool ended = true, long? up = null, long? down = null)
        {
            var rows = flows.ToList();
            string path = Path.Combine(data, "sessions", id);
            Directory.CreateDirectory(path);
            var started = rows.Select(f => f.TimestampUtc).DefaultIfEmpty(today.ToUniversalTime()).Min();
            var updated = rows.Select(f => f.TimestampUtc).DefaultIfEmpty(today.ToUniversalTime()).Max();
            WriteRecord(path, new SessionRecord { Id = id, StartedUtc = started, UpdatedUtc = updated, EndedUtc = ended ? updated : null,
                Status = ended ? "completed" : "recording", UploadBytes = up ?? rows.Sum(f => f.UploadBytes), DownloadBytes = down ?? rows.Sum(f => f.DownloadBytes) });
            File.WriteAllText(Path.Combine(path, "flows.jsonl"), string.Concat(rows.Select(f => JsonSerializer.Serialize(f) + "\n")), new UTF8Encoding(false));
            return path;
        }

        string multi = Path.Combine(root, "multiple");
        string firstSession = MakeSession(multi, "one", new[] { Flow(today.AddDays(-1).AddHours(12), 100, 200) });
        MakeSession(multi, "two", new[] { Flow(today.AddHours(1), 300, 500) });
        var analytics = new HistoryAnalytics(multi);
        Check(analytics.GetSnapshot(DashboardRange.All).IsLoading, "History snapshot remains responsive before initial background indexing");
        analytics.Refresh();
        var all = analytics.GetSnapshot(DashboardRange.All);
        Check(all.UploadBytes == 400 && all.DownloadBytes == 700 && all.SessionCount == 2 && all.RecordedDays == 2,
            "All-time dashboard adds multiple saved sessions and recorded local days");
        analytics.Refresh();
        var restarted = new HistoryAnalytics(multi); restarted.Refresh();
        Check(analytics.GetSnapshot(DashboardRange.All).UploadBytes == 400 && restarted.GetSnapshot(DashboardRange.All).UploadBytes == 400,
            "Repeated refresh and application restart do not double-count persisted history");
        using (var exclusive = new FileStream(Path.Combine(firstSession, "flows.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            analytics.Refresh();
            Check(analytics.GetSnapshot(DashboardRange.All).UploadBytes == 400 && analytics.GetSnapshot(DashboardRange.All).Warning.Length == 0,
                "Unchanged completed sessions use their cached index without reopening the flow file");
        }
        all.Targets[0].UploadBytes = 99999;
        Check(analytics.GetSnapshot(DashboardRange.All).Targets.Sum(t => t.UploadBytes) == 400,
            "Data-bound snapshot mutations do not change the history index");

        string many = Path.Combine(root, "many");
        for (int i = 0; i < 105; i++) MakeSession(many, i.ToString("D3"), new[] { Flow(today.AddDays(-1).AddHours(12), 1, 2) });
        var manyIndex = new HistoryAnalytics(many); manyIndex.Refresh();
        Check(manyIndex.GetSnapshot(DashboardRange.All).SessionCount == 105 && manyIndex.GetSnapshot(DashboardRange.All).UploadBytes == 105,
            "All-time accounting includes sessions beyond the old 100-session display limit");

        string append = Path.Combine(root, "append");
        string active = MakeSession(append, "active", new[] { Flow(today.AddHours(1), 10, 20) }, ended: false);
        var activeIndex = new HistoryAnalytics(append); activeIndex.Refresh();
        var next = Flow(today.AddHours(2), 30, 40, "api.example.test", pid: 99, localPort: 52525);
        AppendBytes(Path.Combine(active, "flows.jsonl"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next) + "\n"));
        UpdateTotals(active, 40, 60);
        activeIndex.Refresh(); activeIndex.Refresh();
        Check(activeIndex.GetSnapshot(DashboardRange.All).UploadBytes == 40 && activeIndex.GetSnapshot(DashboardRange.All).DownloadBytes == 60,
            "An active session's appended flush is indexed exactly once without adding its live ledger again");

        var unicode = Flow(today.AddHours(3), 7, 9); unicode.Process = "codex-测.exe";
        byte[] unicodeLine = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unicode, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        int split = Array.IndexOf(unicodeLine, (byte)0xe6) + 1;
        Check(split > 1, "UTF-8 fixture splits inside a multibyte process name");
        AppendBytes(Path.Combine(active, "flows.jsonl"), unicodeLine[..split]);
        activeIndex.Refresh();
        Check(activeIndex.GetSnapshot(DashboardRange.All).UploadBytes == 40 && !activeIndex.GetSnapshot(DashboardRange.All).Warning.Contains("损坏"),
            "An in-progress UTF-8 trailing line is deferred without being reported as corrupt");
        AppendBytes(Path.Combine(active, "flows.jsonl"), unicodeLine[split..]); UpdateTotals(active, 47, 69);
        activeIndex.Refresh(); activeIndex.Refresh();
        Check(activeIndex.GetSnapshot(DashboardRange.All).UploadBytes == 47 && activeIndex.GetSnapshot(DashboardRange.All).Targets.Any(t => t.Process == unicode.Process),
            "Completing a partial UTF-8 line preserves its name and counts its bytes exactly once");

        string midnight = Path.Combine(root, "midnight");
        MakeSession(midnight, "cross-midnight", new[] { Flow(today.AddMinutes(-1), 11, 13), Flow(today.AddMinutes(1), 17, 19),
            Flow(today.AddDays(-6).AddHours(12), 23, 29), Flow(today.AddDays(-7).AddHours(12), 31, 37), Flow(today.AddDays(-30).AddHours(12), 41, 43) });
        var dayIndex = new HistoryAnalytics(midnight); dayIndex.Refresh();
        var daily = dayIndex.GetSnapshot(DashboardRange.All).DailyUsage;
        Check(daily.Single(d => d.Date == today.AddDays(-1)).UploadBytes == 11 && daily.Single(d => d.Date == today).UploadBytes == 17,
            "A session crossing midnight is split by each record's local date rather than session start");
        Check(dayIndex.GetSnapshot(DashboardRange.Today).UploadBytes == 17 && dayIndex.GetSnapshot(DashboardRange.Last7Days).UploadBytes == 51 &&
            dayIndex.GetSnapshot(DashboardRange.Last30Days).UploadBytes == 82 && dayIndex.GetSnapshot(DashboardRange.All).UploadBytes == 123,
            "Today, seven-day, thirty-day and all-time ranges include exact calendar boundaries");
        Check(dayIndex.GetSnapshot(DashboardRange.All).TodayUploadBytes == 17 && dayIndex.GetSnapshot(DashboardRange.Last7Days).TodayDownloadBytes == 19,
            "Today's counters remain today's bytes when another dashboard range is selected");

        string destinations = Path.Combine(root, "destinations");
        MakeSession(destinations, "routes", new[] { Flow(today.AddHours(1), 10, 20, "first.example.test"), Flow(today.AddHours(2), 30, 40, "second.example.test"),
            Flow(today.AddHours(3), 50, 60), Flow(today.AddHours(4), 70, 80, "first.example.test", pid: 45, localPort: 61001) });
        var routeIndex = new HistoryAnalytics(destinations); routeIndex.Refresh();
        var targetRows = routeIndex.GetSnapshot(DashboardRange.All).Targets;
        Check(targetRows.Count == 3 && targetRows.Single(t => t.Hostname == "first.example.test").UploadBytes == 80 &&
            targetRows.Single(t => t.Hostname == "").UploadBytes == 50 && targetRows.All(t => t.Pid == 0 && t.LocalPort == 0),
            "Historical targets merge socket fragments while keeping interval-specific proxy hosts and unknown destinations separate");
        var nodeOne = Flow(today.AddHours(1), 5, 6, "api.example.test");
        nodeOne.ProxyNode = "Tokyo 01"; nodeOne.Region = "日本 / 东京"; nodeOne.RegionSource = "本地 IP 库";
        var nodeTwo = Flow(today.AddHours(2), 7, 8, "api.example.test");
        nodeTwo.ProxyReportedChain = "Auto → Singapore 02";
        MakeSession(destinations, "nodes", new[] { nodeOne, nodeTwo }); routeIndex.Refresh();
        var nodeRows = routeIndex.GetSnapshot(DashboardRange.All).Targets.Where(t => t.Hostname == "api.example.test").ToList();
        Check(nodeRows.Count == 2 && nodeRows.Single(t => t.ProxyNode == "Tokyo 01").Region == "日本 / 东京" &&
            nodeRows.Single(t => t.ProxyNode == "Singapore 02").UploadBytes == 7,
            "Historical aggregation preserves distinct proxy nodes, recorded region evidence and legacy chain node names");

        string broken = Path.Combine(root, "broken");
        string damaged = MakeSession(broken, "damaged", new[] { Flow(today.AddHours(1), 10, 20) }, up: 100, down: 200);
        AppendBytes(Path.Combine(damaged, "flows.jsonl"), Encoding.UTF8.GetBytes("{bad-json}\n"));
        var damagedIndex = new HistoryAnalytics(broken); damagedIndex.Refresh(); damagedIndex.Refresh();
        var recovered = damagedIndex.GetSnapshot(DashboardRange.All);
        Check(recovered.UploadBytes == 100 && recovered.DownloadBytes == 200 && recovered.Targets.Sum(t => t.UploadBytes) == 10 &&
            recovered.Warning.Contains("1 条损坏") && recovered.Warning.Contains("无法分配"),
            "A corrupt line raises an explicit warning and canonical totals are recovered without inventing target detail");
        Check(damagedIndex.GetSnapshot(DashboardRange.Today).UploadBytes == 10 && damagedIndex.GetSnapshot(DashboardRange.Today).Warning.Contains("可能偏低"),
            "A corrupt session never assigns missing bytes to the session start day");
        string partialEnded = MakeSession(broken, "partial-ended", new[] { Flow(today.AddHours(1), 1, 2) });
        AppendBytes(Path.Combine(partialEnded, "flows.jsonl"), Encoding.UTF8.GetBytes("{\"TimestampUtc\":")); damagedIndex.Refresh();
        Check(damagedIndex.GetSnapshot(DashboardRange.All).Warning.Contains("末尾记录不完整"),
            "A truncated tail in an ended session raises an explicit incomplete-record warning");
        File.Delete(Path.Combine(partialEnded, "session.json")); damagedIndex.Refresh();
        Check(damagedIndex.GetSnapshot(DashboardRange.All).Warning.Contains("摘要无法读取") && damagedIndex.GetSnapshot(DashboardRange.All).UploadBytes == 101,
            "A deleted metadata file is reported while its valid persisted flow remains available");

        string canonical = Path.Combine(root, "canonical");
        string behind = MakeSession(canonical, "behind", new[] { Flow(today.AddHours(1), 90, 110) }, up: 1, down: 2);
        var canonicalIndex = new HistoryAnalytics(canonical); canonicalIndex.Refresh();
        Check(canonicalIndex.GetSnapshot(DashboardRange.All).UploadBytes == 90 && canonicalIndex.GetSnapshot(DashboardRange.All).Warning.Contains("摘要与明细"),
            "A stale lower session summary cannot reduce a larger persisted flow total");
        File.WriteAllText(Path.Combine(behind, "flows.jsonl"), JsonSerializer.Serialize(Flow(today.AddHours(1), 3, 4)) + "\n");
        UpdateTotals(behind, 3, 4); canonicalIndex.Refresh();
        Check(canonicalIndex.GetSnapshot(DashboardRange.All).UploadBytes == 3,
            "Truncated or replaced flow files rebuild their session instead of retaining stale bytes");
        File.Delete(Path.Combine(behind, "flows.jsonl")); canonicalIndex.Refresh();
        Check(canonicalIndex.GetSnapshot(DashboardRange.All).UploadBytes == 3 && canonicalIndex.GetSnapshot(DashboardRange.All).Targets.Count == 0 && canonicalIndex.GetSnapshot(DashboardRange.All).Warning.Contains("无法读取"),
            "A missing flow file retains canonical totals and explicitly reports unavailable detail");

        string empty = Path.Combine(root, "empty");
        var emptyIndex = new HistoryAnalytics(empty); emptyIndex.Refresh();
        Check(!emptyIndex.GetSnapshot(DashboardRange.All).HasRecords && !emptyIndex.GetSnapshot(DashboardRange.All).IsLoading,
            "An empty history finishes loading with an explicit no-record state");
        MakeSession(empty, "zero", Array.Empty<FlowRecord>()); emptyIndex.Refresh();
        Check(emptyIndex.GetSnapshot(DashboardRange.All).HasRecords && emptyIndex.GetSnapshot(DashboardRange.All).SessionCount == 1 && emptyIndex.GetSnapshot(DashboardRange.All).RecordedDays == 0,
            "A valid zero-byte session is recorded without inventing a day of traffic");

        string budgetData = Path.Combine(root, "idle-budget");
        var budgetFlows = Enumerable.Range(0, 2000).Select(i => Flow(today.AddHours(1), 1, 2, "target-" + i + ".example"));
        string budgetSession = MakeSession(budgetData, "many-targets", budgetFlows, ended: false);
        var budgetIndex = new HistoryAnalytics(budgetData);
        budgetIndex.Refresh(); budgetIndex.Refresh();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) budgetIndex.Refresh();
        long refreshAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(refreshAllocation < 2 * 1024 * 1024 && budgetIndex.GetSnapshot(DashboardRange.All).UploadBytes == 2000,
            "Idle history polling avoids allocations proportional to all saved targets while preserving totals");
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) { UpdateTotals(budgetSession, 2000, 4000); budgetIndex.Refresh(); }
        Check(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 2 * 1024 * 1024 && budgetIndex.GetSnapshot(DashboardRange.All).DownloadBytes == 4000,
            "Recording heartbeat metadata without new bytes does not rebuild the dashboard");

        DateTime clock = today.AddHours(12).ToUniversalTime();
        var midnightIndex = new HistoryAnalytics(multi, () => clock);
        midnightIndex.Refresh();
        long beforeMidnight = midnightIndex.GetSnapshot(DashboardRange.Today).UploadBytes;
        clock = clock.AddMinutes(15);
        Check(midnightIndex.GetSnapshot(DashboardRange.Today).ToUtc == clock && midnightIndex.GetSnapshot(DashboardRange.Today).UploadBytes == beforeMidnight,
            "Cached dashboard query time advances without changing persisted counters");
        clock = today.AddDays(1).AddHours(12).ToUniversalTime();
        midnightIndex.Refresh();
        Check(beforeMidnight > 0 && midnightIndex.GetSnapshot(DashboardRange.Today).UploadBytes == 0 && midnightIndex.GetSnapshot(DashboardRange.All).UploadBytes == analytics.GetSnapshot(DashboardRange.All).UploadBytes,
            "Unchanged cached history recomputes date ranges across local midnight");

        return checks;
    }

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes); stream.Flush(true);
    }
    private static void WriteRecord(string path, SessionRecord record) => File.WriteAllText(Path.Combine(path, "session.json"), JsonSerializer.Serialize(record));
    private static void UpdateTotals(string path, long upload, long download)
    {
        var record = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(Path.Combine(path, "session.json")))!;
        record.UploadBytes = upload; record.DownloadBytes = download; record.UpdatedUtc = DateTime.UtcNow;
        WriteRecord(path, record);
    }
}
