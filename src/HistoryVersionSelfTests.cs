using System.IO;
using System.Text.Json;

namespace CodexTraffic;

internal static class HistoryVersionSelfTests
{
    internal static IEnumerable<string> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexHistoryVersionTests", Guid.NewGuid().ToString("N"));
        DateTime now = DateTime.Now.Date.AddHours(12).ToUniversalTime();
        var history = new HistoryAnalytics(root, () => now);
        history.Refresh();
        object first = history.PublishedVersion;
        history.Refresh();
        if (!ReferenceEquals(first, history.PublishedVersion)) throw new InvalidOperationException("Idle history invalidated its UI cache.");
        yield return "Unchanged history reuses the publication identity for idle UI refreshes";
        string session = Path.Combine(root, "sessions", "fixture");
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "session.json"), JsonSerializer.Serialize(new SessionRecord
        { Id = "fixture", StartedUtc = now, UpdatedUtc = now, UploadBytes = 12, DownloadBytes = 24 }));
        File.WriteAllText(Path.Combine(session, "flows.jsonl"), JsonSerializer.Serialize(new FlowRecord
        { TimestampUtc = now, Process = "codex.exe", Protocol = "TCP", RemoteAddress = "203.0.113.1", RemotePort = 443, UploadBytes = 12, DownloadBytes = 24 }) + "\n");
        history.Refresh();
        object changed = history.PublishedVersion;
        if (ReferenceEquals(first, changed) || history.GetSnapshot(DashboardRange.All).UploadBytes != 12)
            throw new InvalidOperationException("New history did not invalidate its UI cache.");
        now = now.AddDays(1);
        history.Refresh();
        if (ReferenceEquals(changed, history.PublishedVersion) || history.GetSnapshot(DashboardRange.Today).UploadBytes != 0)
            throw new InvalidOperationException("Midnight did not invalidate its UI cache.");
        yield return "New persisted data and local midnight invalidate cached UI summaries";
        Directory.Delete(root, true);
        history.Refresh();
        if (history.GetSnapshot(DashboardRange.All).UploadBytes != 0)
            throw new InvalidOperationException("History deletion left stale cached totals.");
        yield return "History deletion invalidates the publication and clears cached totals";
    }
}
