using System.IO;
using System.Text.Json;

namespace CodexTraffic;

public static class LightweightEngineSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "CodexLightweightEngineTests", Guid.NewGuid().ToString("N"));
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name + " (fixtures: " + directory + ")");
            checks.Add(name);
        }

        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        using (var store = new SessionStore(directory, true, true, () => now))
        {
            string summary = Path.Combine(store.DirectoryPath, "session.json");
            string initial = File.ReadAllText(summary);
            for (int i = 1; i <= 29; i++)
            {
                now = now.AddSeconds(1);
                store.Update(0, 0, 0);
            }
            Check(File.ReadAllText(summary) == initial && store.Record.UpdatedUtc == store.Record.StartedUtc,
                "Idle recording avoids 29 unchanged per-second summary rewrites");
            now = now.AddSeconds(1); store.Update(0, 0, 0);
            Check(store.Record.UpdatedUtc == now && File.ReadAllText(summary) != initial,
                "A 30-second idle heartbeat bounds crash-recovery session duration");
            now = now.AddSeconds(1); store.Update(12, 19, 0, claudeUpload: 5, claudeDownload: 7);
            var saved = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(summary))!;
            Check(saved.UpdatedUtc == now && saved.CodexUploadBytes == 7 && saved.ClaudeDownloadBytes == 7,
                "New traffic immediately persists canonical application counters before the idle heartbeat");
            now = now.AddSeconds(1); store.Update(12, 19, 2, claudeUpload: 5, claudeDownload: 7);
            Check(store.Record.EventsLost == 2 && store.Record.UpdatedUtc == now,
                "An ETW-loss change immediately persists even when byte totals are unchanged");

            string before = File.ReadAllText(summary);
            var beforeRecord = store.Record;
            bool failed = false;
            using (new FileStream(summary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Update(13, 19, 2, claudeUpload: 5, claudeDownload: 7); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
            }
            Check(failed && ReferenceEquals(store.Record, beforeRecord) && File.ReadAllText(summary) == before,
                "A failed atomic summary replacement preserves the last durable in-memory record");
            store.Update(13, 19, 2, claudeUpload: 5, claudeDownload: 7);
            Check(store.Record.UploadBytes == 13 && JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(summary))!.UploadBytes == 13,
                "Identical summary input remains retryable after a failed write");

            now = now.AddSeconds(1); store.Update(13, 19, 2, "fixture error", ended: true, claudeUpload: 5, claudeDownload: 7);
            Check(store.Record.Status == "error" && store.Record.Error == "fixture error" && store.Record.EndedUtc == now,
                "Stopping an idle or failed recording immediately persists its final timestamp and error");
            string ended = File.ReadAllText(summary);
            now = now.AddMinutes(2); store.Update(13, 19, 2, "fixture error", ended: true, claudeUpload: 5, claudeDownload: 7);
            Check(File.ReadAllText(summary) == ended,
                "Repeated completion does not rewrite a finished summary or extend its duration");
        }

        var ledger = new TrafficLedger();
        var at = new DateTime(2026, 11, 10, 23, 59, 59, DateTimeKind.Local);
        void Add(DateTime timestamp, string path, string protocol, int localPort, TrackedApplication app, long bytes = 1)
            => ledger.Add(timestamp, 101, "node.exe", path, "tool", protocol, "127.0.0.1", localPort, "127.0.0.1", 7897, bytes, true, app);
        Add(at, "C:\\one.exe", "TCP", 51000, TrackedApplication.Codex);
        Add(at, "C:\\one.exe", "TCP", 51000, TrackedApplication.Codex);
        Add(at.AddSeconds(2), "C:\\one.exe", "TCP", 51000, TrackedApplication.Codex);
        Add(at, "C:\\two.exe", "TCP", 51000, TrackedApplication.Codex);
        Add(at, "C:\\one.exe", "UDP", 51000, TrackedApplication.Codex);
        Add(at, "C:\\one.exe", "TCP", 51001, TrackedApplication.Codex);
        Add(at, "C:\\one.exe", "TCP", 51000, TrackedApplication.Claude);
        Check(ledger.Pending().Count == 6 && ledger.Targets().Count == 4 && ledger.Processes().Count == 3 && ledger.UploadBytes == 7,
            "Compact identities preserve application, protocol, source-port, executable-path and local-day interval boundaries");
        Check(ledger.HasPending && ledger.Pending().All(f => f.Route.Contains("回环")),
            "Reused target classification reaches every persisted interval");
        ledger.CommitPending();
        Check(!ledger.HasPending && ledger.UploadBytes == 7,
            "The allocation-free pending check does not reset lifetime accounting");

        var hotLedger = new TrafficLedger();
        void AddHot() => hotLedger.Add(at, 102, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, 1, true);
        for (int i = 0; i < 256; i++) AddHot();
        long beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        const int transfers = 20_000;
        for (int i = 0; i < transfers; i++) AddHot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        Check(allocated < transfers * 16L && hotLedger.UploadBytes == transfers + 256 && hotLedger.Pending().Count == 1,
            $"Warmed packet accounting allocates {allocated:N0} bytes for {transfers:N0} transfers while retaining exact totals");
        return checks;
    }
}
