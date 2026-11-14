using System.IO;
using System.Net;
using System.Reflection;

namespace CodexTraffic;

internal static class SnapshotConcurrencySelfTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);

    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }

        foreach (var scenario in new[] { (Hint: "DNS", Export: false), (Hint: "GeoIP", Export: false), (Hint: "DNS", Export: true) })
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexSnapshotConcurrency", Guid.NewGuid().ToString("N"));
            using var engine = new MonitorEngine(directory);
            // Keep native scans out of this deterministic fixture. Invoke the
            // production packet callback directly, without starting an ETW session.
            Field<System.Threading.Timer>(engine, "_timer").DisposeAsync().GetAwaiter().GetResult();
            var dns = Field<DnsCacheHints>(engine, "_dns");
            Field<System.Threading.Timer>(dns, "_timer").DisposeAsync().GetAwaiter().GetResult();
            Field<LocalProxyHints>(engine, "_proxy").Dispose();
            var catalog = Field<ProcessCatalog>(engine, "_catalog");
            var at = DateTime.UtcNow.AddSeconds(-1);
            const int codexPid = 1_900_100_001, claudePid = 1_900_100_002;
            catalog.RecordStarted(codexPid, 0, "codex.exe", at, @"C:\fixture\codex.exe");
            catalog.RecordStarted(claudePid, 0, "claude.exe", at,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe"));
            var transfer = typeof(MonitorEngine).GetMethod("Transfer", Private)!;
            void Add(int pid, long bytes) => transfer.Invoke(engine, new object[] {
                DateTime.UtcNow, pid, "UDP", true, IPAddress.Loopback, 51321, IPAddress.Parse("8.8.8.8"), 443, bytes });
            using var store = new SessionStore(directory, true, true);
            Set(engine, "_store", store);
            Set(engine, "_startedAt", DateTime.Now);
            Set(engine, "_running", true);
            try
            {
                Add(codexPid, 100); Add(claudePid, 200);
                var initial = engine.GetSnapshot();
                Check(initial.UploadBytes == 300 && initial.CodexUploadBytes == 100 && initial.ClaudeUploadBytes == 200,
                    $"{scenario}: fixture packets enter the real callback with independent application ownership");
                string expectedCsv = Path.Combine(directory, "expected.csv"), actualCsv = Path.Combine(directory, "actual.csv");
                SessionStore.WriteCsv(expectedCsv, initial.Targets);

                object hintGate = Field<object>(scenario.Hint == "DNS" ? (object)dns : Field<GeoIpHints>(engine, "_geo"), "_gate");
                using var began = new ManualResetEventSlim();
                using var ingested = new ManualResetEventSlim();
                MonitorSnapshot? snapshot = null, lightweight = null;
                Exception? snapshotError = null, producerError = null;
                DateTime addedAt = default;
                var reader = new Thread(() =>
                {
                    try
                    {
                        began.Set();
                        if (scenario.Export) engine.ExportCsv(actualCsv);
                        else snapshot = engine.GetSnapshot();
                    }
                    catch (Exception e) { snapshotError = e; }
                }) { IsBackground = true };
                var producer = new Thread(() =>
                {
                    try
                    {
                        addedAt = DateTime.Now;
                        Add(codexPid, 7); Add(claudePid, 11);
                        lightweight = engine.GetSnapshot(includeTargets: false);
                    }
                    catch (Exception e) { producerError = e; }
                    finally { ingested.Set(); }
                }) { IsBackground = true };
                bool blocked = false, ingestionFinished = false;
                lock (hintGate)
                {
                    reader.Start();
                    if (began.Wait(TimeSpan.FromSeconds(5)))
                        blocked = SpinWait.SpinUntil(() => (reader.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                            TimeSpan.FromSeconds(5));
                    producer.Start();
                    ingestionFinished = ingested.Wait(TimeSpan.FromSeconds(2));
                }
                Check(reader.Join(10000) && producer.Join(10000), $"{scenario}: fixture workers finish after the hint lock is released");
                if (snapshotError != null) throw new InvalidOperationException("Snapshot worker failed", snapshotError);
                if (producerError != null) throw new InvalidOperationException("Packet worker failed", producerError);
                Check(blocked && ingestionFinished && lightweight?.UploadBytes == 318 && lightweight.Targets.Count == 0,
                    $"{scenario}: blocked hint enrichment leaves packet ingestion and lightweight snapshots responsive");
                if (scenario.Export)
                    Check(File.ReadAllText(actualCsv) == File.ReadAllText(expectedCsv),
                        "CSV export retains its original byte snapshot while new packets are ingested");
                else
                {
                    Check(snapshot != null && snapshot.UploadBytes == 300 && snapshot.Targets.Sum(t => t.UploadBytes) == 300 &&
                        snapshot.Processes.Sum(p => p.UploadBytes) == 300 && snapshot.Sessions[0].UploadBytes == 300 &&
                        snapshot.CapturedAt <= addedAt && snapshot.Targets[0].UploadBytes == 200,
                        $"{scenario.Hint}: delayed snapshot retains coherent counters, sample time and descending target order");
                    snapshot!.Targets[0].UploadBytes = -1;
                    snapshot.Processes[0].UploadBytes = -1;
                    snapshot.Sessions[0].UploadBytes = -1;
                }
                var current = engine.GetSnapshot();
                Check(current.UploadBytes == 318 && current.Targets.Sum(t => t.UploadBytes) == 318 &&
                    current.Processes.Sum(p => p.UploadBytes) == 318 && current.Sessions[0].UploadBytes == 318 &&
                    engine.GetSnapshot(applicationScope: DashboardApplicationScope.Codex).UploadBytes == 107 &&
                    engine.GetSnapshot(applicationScope: DashboardApplicationScope.Claude).UploadBytes == 211,
                    $"{scenario}: new snapshots retain all later packets and cannot be changed by earlier callers");
            }
            finally
            {
                Set(engine, "_running", false);
                Set(engine, "_store", null);
            }
        }
        return checks;
    }
}
