using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

public static class TrafficScopeSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        string data = Path.Combine(Path.GetTempPath(), "CodexTrafficScopeTests", Guid.NewGuid().ToString("N"));
        DateTime today = DateTime.Today;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name + " (fixtures: " + data + ")");
            checks.Add(name);
        }
        FlowRecord Row(DateTime local, long up, string type, string node, string host) => new()
        {
            TimestampUtc = local.ToUniversalTime(), Process = "codex.exe", Protocol = "TCP", LocalAddress = "127.0.0.1", LocalPort = 51000,
            RemoteAddress = "127.0.0.1", RemotePort = 7897, ProxyReportedHost = host, ProxyReportedPort = 443,
            ProxyType = type, ProxyNode = node, ProxyReportedChain = node.Length > 0 ? "Auto → " + node : "",
            UploadBytes = up, DownloadBytes = up * 10
        };
        string Session(string id, IEnumerable<FlowRecord> input, long extraUp = 0, long extraDown = 0)
        {
            var rows = input.ToList();
            string directory = Path.Combine(data, "sessions", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "flows.jsonl"), string.Concat(rows.Select(r => JsonSerializer.Serialize(r) + "\n")), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new SessionRecord
            {
                Id = id, StartedUtc = rows.Min(r => r.TimestampUtc), UpdatedUtc = rows.Max(r => r.TimestampUtc), EndedUtc = rows.Max(r => r.TimestampUtc), Status = "completed",
                UploadBytes = rows.Sum(r => r.UploadBytes) + extraUp, DownloadBytes = rows.Sum(r => r.DownloadBytes) + extraDown
            }));
            return directory;
        }

        Check(TrafficClassifier.Classify("Direct", "Tokyo", "Auto → Tokyo").Kind == TrafficKind.Direct &&
            TrafficClassifier.Classify("DIRECT", "", "").Kind == TrafficKind.Direct && TrafficClassifier.Classify("直连", "", "").Kind == TrafficKind.Direct,
            "Explicit Direct outbound types take precedence over a misleading node name");
        Check(TrafficClassifier.Classify("vLeSs", "", "").Kind == TrafficKind.Proxy && TrafficClassifier.Classify("Socks5", "", "").Kind == TrafficKind.Proxy,
            "Known outbound proxy types are recognized without case sensitivity");
        Check(new[] { "Unknown", "Selector", "URLTest", "Fallback", "Relay", "Reject", "RejectDrop", "Pass", "PassRule", "Compatible", "Dns", "Rematch" }
            .All(t => TrafficClassifier.Classify(t, "Tokyo node", "Auto → Tokyo node").Kind == TrafficKind.Unknown),
            "Explicit unknown, group and non-network outbound types never fall back to a proxy-looking chain");
        Check(TrafficClassifier.Classify("", "DIRECT", "Auto → DIRECT").Kind == TrafficKind.Direct &&
            TrafficClassifier.Classify("", "Reject", "Auto → Reject").Kind == TrafficKind.Unknown && TrafficClassifier.Classify("", "", "").Kind == TrafficKind.Unknown,
            "Legacy DIRECT remains direct and absent or rejected node evidence remains unknown");
        var legacy = TrafficClassifier.Classify("", "Tokyo 01", "Auto → Tokyo 01");
        Check(legacy.Kind == TrafficKind.Proxy && legacy.Source.Contains("历史") && legacy.Source.Contains("未记录出口类型"),
            "A legacy proxy node is classified with an explicit historical-evidence explanation");

        string active = Session("mixed", new[]
        {
            Row(today.AddDays(-1).AddHours(12), 10, "Vless", "Tokyo", "yesterday-proxy.test"),
            Row(today.AddHours(1), 20, "Shadowsocks", "Tokyo", "today-proxy.test"),
            Row(today.AddHours(2), 30, "Direct", "DIRECT", "direct.test"),
            Row(today.AddHours(3), 40, "", "", "unconfirmed.test"),
            Row(today.AddHours(4), 50, "", "Legacy node", "legacy-proxy.test"),
            Row(today.AddHours(5), 60, "Unknown", "Proxy-looking node", "unknown-type.test")
        }, extraUp: 7, extraDown: 9);
        var history = new HistoryAnalytics(data); history.Refresh();
        var all = history.GetSnapshot(DashboardRange.All);
        var proxy = history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.Proxy);
        Check(all.UploadBytes == 217 && all.DownloadBytes == 2109 && all.AllUploadBytes == 217 && all.TrafficScope == DashboardTrafficScope.All,
            "The default dashboard retains all canonical directional traffic");
        Check(all.ProxyUploadBytes == 80 && all.DirectUploadBytes == 30 && all.UnknownUploadBytes == 107 &&
            all.ProxyDownloadBytes == 800 && all.DirectDownloadBytes == 300 && all.UnknownDownloadBytes == 1009 &&
            all.AllUploadBytes == all.ProxyUploadBytes + all.DirectUploadBytes + all.UnknownUploadBytes &&
            all.AllDownloadBytes == all.ProxyDownloadBytes + all.DirectDownloadBytes + all.UnknownDownloadBytes,
            "All traffic equals proxy plus direct plus unknown, with missing canonical detail assigned only to unknown");
        Check(proxy.UploadBytes == 80 && proxy.DownloadBytes == 800 && proxy.AllUploadBytes == 217 && proxy.UnknownUploadBytes == 107 &&
            proxy.Targets.Count == 3 && proxy.Targets.All(t => t.TrafficKind == TrafficKind.Proxy) && proxy.DailyUsage.Sum(d => d.UploadBytes) == 80,
            "Proxy scope filters counters, target rows and daily usage without changing the all-traffic breakdown");
        var proxyToday = history.GetSnapshot(DashboardRange.Today, DashboardTrafficScope.Proxy);
        var allToday = history.GetSnapshot(DashboardRange.Today);
        Check(proxy.TodayUploadBytes == 70 && all.TodayUploadBytes == 200 && proxyToday.UploadBytes == 70 && allToday.UploadBytes == 200 &&
            proxyToday.AllUploadBytes == 200 && proxyToday.DirectUploadBytes == 30 && proxyToday.UnknownUploadBytes == 100,
            "Today's scope and directional breakdown use record dates and never allocate missing summary bytes to today");
        Check(history.GetSnapshot(DashboardRange.Last7Days, DashboardTrafficScope.Proxy).UploadBytes == 80 &&
            history.GetSnapshot(DashboardRange.Last30Days).AllUploadBytes == 210 && proxy.RecordedDays == 2,
            "Seven-day and thirty-day scopes preserve calendar-range accounting");
        history.Refresh(); var restarted = new HistoryAnalytics(data); restarted.Refresh();
        Check(history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.Proxy).UploadBytes == 80 &&
            restarted.GetSnapshot(DashboardRange.All).UploadBytes == 217 && restarted.GetSnapshot(DashboardRange.All, DashboardTrafficScope.Proxy).UploadBytes == 80,
            "Repeated refresh and restart preserve both legacy classification and once-only persisted accounting");

        SessionStore.WriteCsv(Path.Combine(data, "proxy.csv"), proxy.Targets);
        string proxyCsv = File.ReadAllText(Path.Combine(data, "proxy.csv"));
        Check(proxyCsv.Contains("TrafficKind,ProxyType,TrafficKindSource") && proxyCsv.Contains("\"Proxy\",\"Shadowsocks\"") &&
            !proxyCsv.Contains("\"direct.test\"") && !proxyCsv.Contains("\"unknown-type.test\"") && File.ReadAllLines(Path.Combine(data, "proxy.csv")).Length == 4,
            "Proxy CSV exports only selected proxy detail and includes classification and outbound-type columns");
        var formula = proxy.Targets[0]; formula.ProxyType = "=DANGEROUS"; formula.ProxyNode = "=NODE";
        SessionStore.WriteCsv(Path.Combine(data, "formula.csv"), new[] { formula });
        string formulaCsv = File.ReadAllText(Path.Combine(data, "formula.csv"));
        Check(formulaCsv.Contains("\"'=DANGEROUS\"") && formulaCsv.Contains("\"'=NODE\""),
            "Added outbound and node CSV fields preserve spreadsheet-formula escaping");

        var collisionOne = Row(today.AddHours(6), 1, "Direct", "Shared", "same.test");
        var collisionTwo = Row(today.AddHours(7), 2, "Vmess", "Shared", "same.test");
        Session("same-endpoint", new[] { collisionOne, collisionTwo }); history.Refresh();
        var same = history.GetSnapshot(DashboardRange.All).Targets.Where(t => t.Hostname == "same.test").ToList();
        Check(same.Count == 2 && same.Single(t => t.TrafficKind == TrafficKind.Direct).UploadBytes == 1 && same.Single(t => t.TrafficKind == TrafficKind.Proxy).UploadBytes == 2,
            "A shared process, node and endpoint retain separate direct and proxy aggregation rows");

        var ledger = new TrafficLedger();
        ledger.Add(DateTime.UtcNow, 99, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, 5, true);
        var flow = ledger.Pending().Single();
        Check(flow.ProxyType == "Unknown", "New unreported intervals are explicitly unknown rather than legacy proxy records");
        ledger.Annotate(flow, new ProxyHint("typed.test", "1.1.1.1", 443, "Auto → typed node", "test", DateTime.UtcNow, "typed node", "", "Vless"));
        Check(flow.ProxyType == "Vless" && ledger.Targets().Single().TrafficKind == TrafficKind.Proxy,
            "Interval annotation persists the controller's outbound type and exposes its classification");
        ledger.CommitPending();
        ledger.Add(DateTime.UtcNow, 99, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, 7, true);
        ledger.MarkUnconfirmed(ledger.Pending().Single());
        Check(ledger.Targets().Single().TrafficKind == TrafficKind.Unknown && ledger.Targets().Single().ProxyType == "Mixed" && ledger.UploadBytes == 12,
            "A live socket with mixed interval evidence stays unknown instead of relabeling its full lifetime as proxy traffic");
        return checks;
    }
}
