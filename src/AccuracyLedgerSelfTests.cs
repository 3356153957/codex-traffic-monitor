using System.Text.Json;

namespace CodexTraffic;

public static class AccuracyLedgerSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + message);
            checks.Add(message);
        }

        var ledger = new TrafficLedger();
        var first = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var middle = first.AddMilliseconds(300);
        var last = first.AddMilliseconds(900);
        void Add(DateTime at, long bytes, bool upload = true, TrackedApplication app = TrackedApplication.Codex)
            => ledger.Add(at, 101, "node.exe", "C:\\node.exe", "tool", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, bytes, upload, app);

        Add(middle, 11); Add(last, 13, false); Add(first, 17);
        var flow = ledger.Pending().Single();
        Check(flow.TimestampUtc == first && flow.LastTimestampUtc == last && flow.UploadBytes == 28 && flow.DownloadBytes == 13,
            "Out-of-order transfers preserve the entire interval's earliest and latest UTC timestamps and directional bytes");
        Check(ledger.Targets().Single().LastSeen.ToUniversalTime() == last && ledger.UploadBytes == 28 && ledger.DownloadBytes == 13,
            "A delayed earlier transfer cannot move the socket's last-seen time backwards or change counted totals");
        string json = JsonSerializer.Serialize(flow);
        var saved = JsonSerializer.Deserialize<FlowRecord>(json)!;
        Check(saved.TimestampUtc == first && saved.LastTimestampUtc == last && saved.UploadBytes == 28 && saved.DownloadBytes == 13,
            "Persisted flow intervals retain both timestamp bounds without changing the byte accounting schema");
        var legacy = JsonSerializer.Deserialize<FlowRecord>("{\"TimestampUtc\":\"2026-11-10T12:00:00Z\",\"UploadBytes\":5}")!;
        Check(legacy.TimestampUtc == first && legacy.LastTimestampUtc == default && legacy.UploadBytes == 5,
            "Older flow records without a last timestamp remain readable with their existing counters");

        Add(first.AddMilliseconds(100), 19, app: TrackedApplication.Claude);
        Add(last.AddMilliseconds(100), 23, false, TrackedApplication.Claude);
        var claude = ledger.Pending().Single(row => row.Application == TrackedApplication.Claude);
        Check(ledger.Pending().Count == 2 && claude.TimestampUtc == first.AddMilliseconds(100) && claude.LastTimestampUtc == last.AddMilliseconds(100) &&
            ledger.Totals(DashboardApplicationScope.Codex) == (28L, 13L) && ledger.Totals(DashboardApplicationScope.Claude) == (19L, 23L),
            "Interval time bounds and bytes remain independent for applications sharing the same PID and socket tuple");
        ledger.CommitPending();
        Add(last.AddSeconds(1), 29);
        var next = ledger.Pending().Single();
        Check(next.TimestampUtc == last.AddSeconds(1) && next.LastTimestampUtc == next.TimestampUtc && next.UploadBytes == 29 && ledger.UploadBytes == 76,
            "Committing an interval starts fresh timestamp bounds without resetting lifetime byte totals");

        var midnight = new TrafficLedger();
        var localMidnight = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Local);
        void AddMidnight(DateTime at, long bytes)
            => midnight.Add(at, 102, "codex.exe", "C:\\codex.exe", "core", "UDP", "127.0.0.1", 51001, "127.0.0.1", 7897, bytes, true);
        AddMidnight(localMidnight.AddMilliseconds(200), 7);
        AddMidnight(localMidnight.AddMilliseconds(-200), 11);
        AddMidnight(localMidnight.AddMilliseconds(-500), 13);
        var dates = midnight.Pending().OrderBy(row => row.TimestampUtc).ToList();
        Check(dates.Count == 2 && dates[0].UploadBytes == 24 && dates[0].TimestampUtc == localMidnight.AddMilliseconds(-500).ToUniversalTime() &&
            dates[0].LastTimestampUtc == localMidnight.AddMilliseconds(-200).ToUniversalTime() && dates[1].UploadBytes == 7 &&
            dates[1].TimestampUtc == dates[1].LastTimestampUtc,
            "Timestamp bounds respect local-day boundaries even when events around midnight arrive out of order");

        // A repeated local hour must still use absolute time for latest activity.
        // These points straddle the 2026 fall-back in America/Los_Angeles; the
        // assertion also remains valid when tests run under another local zone.
        var fallback = new TrafficLedger();
        var early = new DateTime(2026, 11, 1, 8, 55, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 11, 1, 9, 15, 0, DateTimeKind.Utc);
        fallback.Add(early, 103, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51002, "127.0.0.1", 7897, 1, true);
        fallback.Add(later, 103, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51002, "127.0.0.1", 7897, 1, true);
        fallback.Add(early.AddMinutes(-1), 103, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51002, "127.0.0.1", 7897, 1, true);
        Check(fallback.Targets().Single().LastSeen.ToUniversalTime() == later && fallback.Pending().Single().LastTimestampUtc == later,
            "Repeated daylight-saving local hours cannot move latest activity backwards in absolute time");
        return checks;
    }
}
