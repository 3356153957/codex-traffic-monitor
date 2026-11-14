using System.Collections;
using System.IO;
using System.Reflection;

namespace CodexTraffic;

internal static class HistoryRangeReuseSelfTests
{
    internal static IEnumerable<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexRangeReuseTests", Guid.NewGuid().ToString("N"));
        DateTime now = DateTime.Today.AddHours(12).ToUniversalTime();
        var history = new HistoryAnalytics(root, () => now);
        void Write(string host, int day, long bytes, TrackedApplication app = TrackedApplication.Codex)
        {
            using var store = new SessionStore(root, true, true, () => now.AddSeconds(day));
            store.Append(new[] { new FlowRecord { TimestampUtc = now.ToLocalTime().Date.AddDays(day).AddHours(1).ToUniversalTime(),
                Process = "tool.exe", Application = app, Protocol = "TCP", RemoteAddress = "203.0.113.1", RemotePort = 443,
                ProxyReportedHost = host, ProxyType = "Vless", UploadBytes = bytes } });
            store.Update(bytes, 0, 0, ended: true, claudeUpload: app == TrackedApplication.Claude ? bytes : 0);
        }
        List<TargetRow> Targets(DashboardRange range)
        {
            var publication = (IDictionary)history.PublishedVersion;
            var saved = publication[(range, DashboardTrafficScope.All)]!;
            return ((DashboardSnapshot)saved.GetType().GetProperty("Total")!.GetValue(saved)!).Targets;
        }
        try
        {
            Write("today.example", 0, 10); history.Refresh();
            var oldList = Targets(DashboardRange.All);
            if (!Enum.GetValues<DashboardRange>().All(range => ReferenceEquals(oldList, Targets(range))))
                throw new InvalidOperationException("Equivalent date ranges failed to reuse their target list");
            yield return "Equivalent date selectors share one immutable target list within a publication";

            Write("yesterday.example", -1, 20, TrackedApplication.Claude); history.Refresh();
            if (Targets(DashboardRange.Today).Count != 1 || Targets(DashboardRange.All).Count != 2 ||
                !ReferenceEquals(Targets(DashboardRange.Last7Days), Targets(DashboardRange.All)) ||
                ReferenceEquals(oldList, Targets(DashboardRange.All)) || oldList.Count != 1 || oldList[0].UploadBytes != 10)
                throw new InvalidOperationException("Appending changed old publication or reused an unequal day set");
            yield return "Appending a new day separates today's list while preserving the old publication unchanged";

            Write("ten-days.example", -10, 40); Write("forty-days.example", -40, 80);
            Write("future.example", 1, 160); history.Refresh();
            foreach (var pair in new[] { (DashboardRange.Today, 10L), (DashboardRange.Last7Days, 30L),
                (DashboardRange.Last30Days, 70L), (DashboardRange.All, 310L) })
            foreach (var scope in Enum.GetValues<DashboardTrafficScope>())
            {
                var snapshot = history.GetSnapshot(pair.Item1, scope);
                if (snapshot.Targets.Sum(t => t.UploadBytes) != pair.Item2 || snapshot.UploadBytes != pair.Item2)
                    throw new InvalidOperationException("Unequal or future-containing date ranges reused incorrect targets");
                var claude = history.GetSnapshot(pair.Item1, scope, DashboardApplicationScope.Claude);
                if (claude.UploadBytes != (pair.Item1 == DashboardRange.Today ? 0 : 20))
                    throw new InvalidOperationException("Reused targets changed an application slice");
            }
            yield return "Different date sets, future rows, proxy scope and application slices retain independent totals";

            now = now.AddDays(2); history.Refresh();
            if (Targets(DashboardRange.Today).Count != 0 || history.GetSnapshot(DashboardRange.All).UploadBytes != 310)
                throw new InvalidOperationException("Crossing midnight retained a stale equivalent range");
            var snapshotCopy = history.GetSnapshot(DashboardRange.All);
            snapshotCopy.Targets.Clear();
            if (Targets(DashboardRange.All).Count != 5) throw new InvalidOperationException("Caller mutated a shared list");
            yield return "A calendar change invalidates range reuse and callers cannot mutate shared target lists";
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
