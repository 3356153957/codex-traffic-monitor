using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodexTraffic;

public static class LedgerMemorySelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "CodexLedgerMemoryTests", Guid.NewGuid().ToString("N"));
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + message + " (fixtures: " + directory + ")");
            checks.Add(message);
        }

        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new TrafficLedger();
        string log;
        using (var store = new SessionStore(directory, true, true))
        {
            log = store.LogFile;
            for (int i = 0; i < 256; i++)
            {
                ledger.Add(at.AddSeconds(i), 101, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, i + 1, true);
                var pending = ledger.Pending();
                ledger.Annotate(pending.Single(), new ProxyHint("host-" + i + ".test", "203.0.113.8", 443, "fixture", "fixture", at, "node", "US", i == 0 ? "Direct" : "Vless"));
                store.Append(pending);
                ledger.CommitPending();
            }
            store.Update(ledger.UploadBytes, ledger.DownloadBytes, 0, ended: true);
        }
        var target = ledger.Targets().Single();
        Check(target.Hostname == "host-0.test / host-1.test / host-2.test 等" &&
            target.ProxyDestination == "多个目标，详见会话明细" && target.DomainSource == "代理报告",
            "A long-lived proxy socket keeps its first three distinct display hosts and the additional-host marker");
        Check(target.ProxyType == "Mixed" && target.TrafficKind == TrafficKind.Unknown &&
            ledger.UploadBytes == 256L * 257 / 2 && target.UploadBytes == ledger.UploadBytes,
            "Compact mixed-policy evidence retains exact lifetime socket and application bytes");
        var saved = File.ReadLines(log).Select(line => JsonSerializer.Deserialize<FlowRecord>(line)!).ToList();
        Check(saved.Count == 256 && saved.Select(row => row.ProxyReportedHost).Distinct().Count() == 256 &&
            saved[255].ProxyReportedHost == "host-255.test" && saved[0].ProxyType == "Direct" && saved[255].ProxyType == "Vless" &&
            saved.Sum(row => row.UploadBytes) == ledger.UploadBytes,
            "Bounded display metadata preserves every original persisted host, policy and byte interval");

        var retained = AddUnshownMetadata(ledger, at.AddMinutes(10));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!retained.Host.IsAlive && !retained.Type.IsAlive && ledger.Targets().Single().ProxyType == "Mixed",
            "Unshown host and obsolete policy strings are collectible after their intervals are committed");

        var many = new TrafficLedger();
        for (int i = 0; i < 2048; i++)
            many.Add(at, i + 1000, "node.exe", "C:\\node.exe", "tool", "TCP", Fresh("127.0.0.1"), i + 10000, Fresh("127.0.0.1"), 7897, i + 1, i % 2 == 0,
                i % 2 == 0 ? TrackedApplication.Codex : TrackedApplication.Claude);
        long upload = many.UploadBytes, download = many.DownloadBytes;
        many.TrimIdleBuffers(at.AddHours(1));
        Check(many.Pending().Count == 2048,
            "Idle buffer trimming never discards uncommitted intervals");
        many.CommitPending();
        many.TrimIdleBuffers(at.AddSeconds(31));
        var rows = many.Targets();
        Check(rows.Count == 2048 && rows.Sum(row => row.UploadBytes) == upload && rows.Sum(row => row.DownloadBytes) == download && many.Processes().Count == 2048 &&
            many.Targets(DashboardApplicationScope.Codex).Count == 1024 && many.Targets(DashboardApplicationScope.Claude).Count == 1024,
            "Releasing idle interval storage preserves every socket, exited-process row and application total for full session export");
        Check(ReferenceEquals(rows[0].LocalAddress, rows[1].LocalAddress) && ReferenceEquals(rows[0].Address, rows[1].Address) && ReferenceEquals(rows[0].Target, rows[1].Target),
            "Repeated socket addresses and endpoint labels share session-local immutable strings");

        var casing = new TrafficLedger();
        casing.Add(at, 1, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, 1, true);
        var flow = casing.Pending().Single();
        casing.Annotate(flow, new ProxyHint("example.test", "203.0.113.9", 443, "fixture", "fixture", at, "node", "", "Vless"));
        casing.Annotate(flow, new ProxyHint("Example.test", "203.0.113.9", 443, "fixture", "fixture", at, "node", "", "vless"));
        Check(casing.Targets().Single().Hostname == "example.test / Example.test" && casing.Targets().Single().ProxyType == "vless" &&
            casing.Targets().Single().TrafficKind == TrafficKind.Proxy,
            "Host distinctness remains ordinal while equivalent outbound type casing does not introduce a mixed policy");
        return checks;
    }

    private static string Fresh(string value) => new(value.ToCharArray());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Host, WeakReference Type) AddUnshownMetadata(TrafficLedger ledger, DateTime at)
    {
        ledger.Add(at, 101, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, 1, true);
        string host = Fresh("a-host-only-retained-by-its-interval.test"), type = Fresh("an-obsolete-custom-outbound-type");
        var references = (new WeakReference(host), new WeakReference(type));
        var flow = ledger.Pending().Single();
        ledger.Annotate(flow, new ProxyHint(host, "203.0.113.8", 443, "fixture", "fixture", at, "node", "", type));
        ledger.CommitPending();
        // The next interval replaces the latest annotation. Mixed classification
        // remains valid without remembering every distinct historical type.
        ledger.Add(at.AddSeconds(1), 101, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, 1, true);
        ledger.Annotate(ledger.Pending().Single(), new ProxyHint("host-0.test", "203.0.113.8", 443, "fixture", "fixture", at, "node", "", "Vless"));
        ledger.CommitPending();
        return references;
    }
}
