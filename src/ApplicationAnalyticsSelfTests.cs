using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

public static class ApplicationAnalyticsSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        string data = Path.Combine(Path.GetTempPath(), "CodexApplicationHistoryTests", Guid.NewGuid().ToString("N"));
        DateTime today = DateTime.Today;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name + " (fixtures: " + data + ")");
            checks.Add(name);
        }
        FlowRecord Row(TrackedApplication application, DateTime date, long up, string proxyType, string host = "shared.test") => new()
        {
            Application = application, TimestampUtc = date.ToUniversalTime(), Process = "node.exe", Protocol = "TCP",
            LocalAddress = "127.0.0.1", LocalPort = 51000, RemoteAddress = "127.0.0.1", RemotePort = 7897,
            ProxyReportedHost = host, ProxyReportedPort = 443, ProxyNode = "Shared", ProxyReportedChain = "Auto → Shared",
            ProxyType = proxyType, UploadBytes = up, DownloadBytes = up * 10
        };
        SessionRecord Summary(string id, List<FlowRecord> rows) => new()
        {
            Id = id, StartedUtc = rows.Select(r => r.TimestampUtc).DefaultIfEmpty(today.ToUniversalTime()).Min(),
            UpdatedUtc = rows.Select(r => r.TimestampUtc).DefaultIfEmpty(today.ToUniversalTime()).Max(),
            EndedUtc = rows.Select(r => r.TimestampUtc).DefaultIfEmpty(today.ToUniversalTime()).Max(), Status = "completed",
            AccountingVersion = 2,
            CodexUploadBytes = rows.Where(r => r.Application == TrackedApplication.Codex).Sum(r => r.UploadBytes),
            CodexDownloadBytes = rows.Where(r => r.Application == TrackedApplication.Codex).Sum(r => r.DownloadBytes),
            ClaudeUploadBytes = rows.Where(r => r.Application == TrackedApplication.Claude).Sum(r => r.UploadBytes),
            ClaudeDownloadBytes = rows.Where(r => r.Application == TrackedApplication.Claude).Sum(r => r.DownloadBytes),
            UploadBytes = rows.Sum(r => r.UploadBytes), DownloadBytes = rows.Sum(r => r.DownloadBytes)
        };
        string WriteSession(string baseDirectory, string id, List<FlowRecord> rows, SessionRecord summary, bool legacyRows = false)
        {
            string directory = Path.Combine(baseDirectory, "sessions", id); Directory.CreateDirectory(directory);
            string Line(FlowRecord row)
            {
                if (!legacyRows) return JsonSerializer.Serialize(row);
                var json = JsonSerializer.SerializeToNode(row)!.AsObject(); json.Remove("Application"); return json.ToJsonString();
            }
            File.WriteAllText(Path.Combine(directory, "flows.jsonl"), string.Concat(rows.Select(r => Line(r) + "\n")), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(summary));
            return directory;
        }
        var rows = new List<FlowRecord>
        {
            Row(TrackedApplication.Codex, today.AddDays(-8).AddHours(1), 2, "Vless", "old.test"),
            Row(TrackedApplication.Codex, today.AddDays(-1).AddHours(1), 10, "Unknown", "yesterday.test"),
            Row(TrackedApplication.Codex, today.AddHours(1), 20, "Vless"),
            Row(TrackedApplication.Codex, today.AddHours(2), 30, "Direct"),
            Row(TrackedApplication.Codex, today.AddHours(3), 40, "Unknown"),
            Row(TrackedApplication.Claude, today.AddHours(1), 50, "Vless"),
            Row(TrackedApplication.Claude, today.AddHours(2), 60, "Direct"),
            Row(TrackedApplication.Claude, today.AddHours(3), 70, "Unknown")
        };
        var summary = Summary("mixed", rows);
        summary.CodexUploadBytes += 5; summary.CodexDownloadBytes += 9;
        summary.ClaudeUploadBytes += 7; summary.ClaudeDownloadBytes += 11;
        summary.UploadBytes += 12; summary.DownloadBytes += 20;
        string mixedDirectory = WriteSession(data, "mixed", rows, summary);
        var history = new HistoryAnalytics(data);
        Check(history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude).IsLoading,
            "Application history queries remain responsive before background indexing");
        history.Refresh();
        var total = history.GetSnapshot(DashboardRange.All);
        var codex = history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Codex);
        var claude = history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude);
        Check(total.ApplicationScope == DashboardApplicationScope.Total && total.UploadBytes == 294 && total.DownloadBytes == 2840 &&
            codex.UploadBytes == 107 && codex.DownloadBytes == 1029 && claude.UploadBytes == 187 && claude.DownloadBytes == 1811,
            "Combined history equals independently canonical Codex and Claude directional totals");
        Check(total.CodexUploadBytes == codex.UploadBytes && total.ClaudeUploadBytes == claude.UploadBytes &&
            claude.CodexDownloadBytes == codex.DownloadBytes && codex.ClaudeDownloadBytes == claude.DownloadBytes,
            "Application comparison counters keep the same date and traffic scope across application tabs");
        Check(codex.ProxyUploadBytes == 22 && codex.DirectUploadBytes == 30 && codex.UnknownUploadBytes == 55 &&
            claude.ProxyUploadBytes == 50 && claude.DirectUploadBytes == 60 && claude.UnknownUploadBytes == 77 &&
            total.AllUploadBytes == total.ProxyUploadBytes + total.DirectUploadBytes + total.UnknownUploadBytes,
            "Each application's all traffic reconciles proxy, direct and unknown with missing details only unknown");
        var claudeProxy = history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.Proxy, DashboardApplicationScope.Claude);
        Check(claudeProxy.UploadBytes == 50 && claudeProxy.DownloadBytes == 500 && claudeProxy.AllUploadBytes == 187 &&
            claudeProxy.Targets.Count == 1 && claudeProxy.Targets.All(t => t.Application == TrackedApplication.Claude && t.TrafficKind == TrafficKind.Proxy) &&
            claudeProxy.DailyUsage.Sum(d => d.UploadBytes) == 50 && claudeProxy.CodexUploadBytes == 22 && claudeProxy.ClaudeUploadBytes == 50,
            "Application and proxy filters intersect totals, comparison counters, destinations and daily usage");
        Check(codex.Targets.Count == 5 && claude.Targets.Count == 3 && total.Targets.Count == 8 &&
            total.Targets.Count(t => t.Hostname == "shared.test" && t.TrafficKind == TrafficKind.Proxy) == 2,
            "Identical process names, endpoints and proxy nodes remain separate for the two applications");
        Check(codex.TodayUploadBytes == 90 && claude.TodayUploadBytes == 180 && total.TodayUploadBytes == 270 &&
            codex.RecordedDays == 3 && claude.RecordedDays == 1 && total.RecordedDays == 3,
            "Application day counts and today's usage follow individual record dates");
        Check(history.GetSnapshot(DashboardRange.Today, DashboardTrafficScope.All, DashboardApplicationScope.Codex).UploadBytes == 90 &&
            history.GetSnapshot(DashboardRange.Last7Days, DashboardTrafficScope.All, DashboardApplicationScope.Codex).UploadBytes == 100 &&
            history.GetSnapshot(DashboardRange.Last30Days, DashboardTrafficScope.All, DashboardApplicationScope.Claude).UploadBytes == 180 &&
            history.GetSnapshot(DashboardRange.Today, DashboardTrafficScope.Proxy, DashboardApplicationScope.Codex).UploadBytes == 20,
            "Date, application and proxy selectors compose at local calendar boundaries");
        foreach (var range in Enum.GetValues<DashboardRange>())
        foreach (var traffic in Enum.GetValues<DashboardTrafficScope>())
        {
            var combined = history.GetSnapshot(range, traffic);
            var c = history.GetSnapshot(range, traffic, DashboardApplicationScope.Codex);
            var a = history.GetSnapshot(range, traffic, DashboardApplicationScope.Claude);
            Check(combined.UploadBytes == c.UploadBytes + a.UploadBytes && combined.DownloadBytes == c.DownloadBytes + a.DownloadBytes &&
                combined.DailyUsage.Sum(d => d.UploadBytes) == c.DailyUsage.Sum(d => d.UploadBytes) + a.DailyUsage.Sum(d => d.UploadBytes),
                $"Combined counters and daily rows reconcile both applications for {range}/{traffic}");
        }
        claude.Targets[0].UploadBytes = long.MaxValue; claude.Targets[0].Application = TrackedApplication.Codex;
        claude.DailyUsage[0].DownloadBytes = long.MaxValue;
        Check(history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude).Targets.Sum(t => t.UploadBytes) == 180 &&
            history.GetSnapshot(DashboardRange.All).DailyUsage.Sum(d => d.DownloadBytes) == 2820,
            "Mutating application snapshot rows cannot change another application, combined snapshot or cached index");
        history.Refresh(); var restarted = new HistoryAnalytics(data); restarted.Refresh();
        Check(restarted.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude).UploadBytes == 187 &&
            history.GetSnapshot(DashboardRange.All).UploadBytes == 294,
            "Application attribution survives refresh and restart without duplicate persisted accounting");
        // The aggregate does not change, but its application allocation does.
        summary.CodexUploadBytes -= 1; summary.ClaudeUploadBytes += 1;
        File.WriteAllText(Path.Combine(mixedDirectory, "session.json"), JsonSerializer.Serialize(summary)); history.Refresh();
        Check(history.GetSnapshot(DashboardRange.All).UploadBytes == 294 &&
            history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Codex).UploadBytes == 106 &&
            history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude).UploadBytes == 188,
            "Application-summary changes invalidate cached counters even when combined bytes are unchanged");

        string legacyData = Path.Combine(data, "legacy");
        var legacyRows = new List<FlowRecord> { Row(TrackedApplication.Codex, today.AddHours(1), 11, "Unknown") };
        var legacySummary = Summary("old", legacyRows); legacySummary.AccountingVersion = 0; legacySummary.UploadBytes = 100; legacySummary.DownloadBytes = 200;
        WriteSession(legacyData, "old", legacyRows, legacySummary, legacyRows: true);
        var legacyHistory = new HistoryAnalytics(legacyData); legacyHistory.Refresh();
        var legacyCodex = legacyHistory.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Codex);
        var legacyClaude = legacyHistory.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude);
        Check(legacyCodex.UploadBytes == 100 && legacyCodex.DownloadBytes == 200 && legacyCodex.Targets.Single().Application == TrackedApplication.Codex &&
            legacyClaude.UploadBytes == 0 && legacyClaude.Targets.Count == 0 && !legacyClaude.HasRecords && legacyClaude.SessionCount == 0,
            "Legacy records without application fields and legacy canonical summaries remain exclusively Codex traffic");
        Check(legacyCodex.SessionCount == 1 && legacyCodex.HasRecords && legacyClaude.FirstRecordedUtc == null,
            "Application session counts and first-record state exclude unrelated legacy sessions");

        string brokenData = Path.Combine(data, "broken-summary");
        var brokenRows = new List<FlowRecord> { Row(TrackedApplication.Claude, today.AddHours(1), 3, "Vless") };
        var brokenSummary = Summary("bad", brokenRows); brokenSummary.UploadBytes += 100;
        WriteSession(brokenData, "bad", brokenRows, brokenSummary);
        var brokenHistory = new HistoryAnalytics(brokenData); brokenHistory.Refresh();
        var broken = brokenHistory.GetSnapshot(DashboardRange.All);
        Check(broken.UploadBytes == 3 && broken.CodexUploadBytes == 0 && broken.ClaudeUploadBytes == 3 && broken.Warning.Contains("摘要无法读取"),
            "An inconsistent new application summary is rejected with a warning instead of guessing ownership of missing bytes");

        string onlyClaudeData = Path.Combine(data, "claude-only");
        var onlyClaudeRows = new List<FlowRecord> { Row(TrackedApplication.Claude, today.AddHours(1), 4, "Direct") };
        WriteSession(onlyClaudeData, "claude", onlyClaudeRows, Summary("claude", onlyClaudeRows));
        var onlyClaudeHistory = new HistoryAnalytics(onlyClaudeData); onlyClaudeHistory.Refresh();
        Check(onlyClaudeHistory.GetSnapshot(DashboardRange.All).SessionCount == 1 &&
            onlyClaudeHistory.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude).SessionCount == 1 &&
            onlyClaudeHistory.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Codex).SessionCount == 0 &&
            !onlyClaudeHistory.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Codex).HasRecords,
            "A Claude-only session does not create a Codex monitoring session");
        return checks;
    }
}
