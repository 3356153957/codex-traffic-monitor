using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CodexTraffic;

// A tray instance that is not capturing has no consumer for live connection,
// proxy, DNS or history data, so it must not keep polling them.
internal static class IdlePollingSelfTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds) { if (condition()) return true; Thread.Sleep(25); }
            return condition();
        }

        Check(MonitorEngine.NeedsLiveData(capturing: true, viewerActive: false) &&
              MonitorEngine.NeedsLiveData(capturing: false, viewerActive: true) &&
              MonitorEngine.NeedsLiveData(capturing: true, viewerActive: true) &&
              !MonitorEngine.NeedsLiveData(capturing: false, viewerActive: false),
            "Live polling is needed while capturing or viewing, and only an idle hidden instance pauses it");

        using (var proxy = new LocalProxyHints(() => Array.Empty<LocalProxyController>(), startTimer: true))
        {
            bool started = proxy.IsPolling;
            proxy.SetPolling(false);
            bool paused = !proxy.IsPolling;
            proxy.SetPolling(false);
            proxy.SetPolling(true);
            Check(started && paused && proxy.IsPolling, "Proxy controller polling can pause and resume idempotently");
        }
        var disposedProxy = new LocalProxyHints(() => Array.Empty<LocalProxyController>(), startTimer: true);
        disposedProxy.Dispose();
        disposedProxy.SetPolling(true);
        Check(!disposedProxy.IsPolling, "A disposed proxy poller never restarts");

        int reads = 0;
        using (var dns = new DnsCacheHints(() => { Interlocked.Increment(ref reads); return Array.Empty<DnsCacheRecord>(); }, () => Environment.TickCount64, startTimer: true))
        {
            bool firstRead = WaitFor(() => Volatile.Read(ref reads) >= 1, 2000);
            dns.SetPolling(false);
            bool paused = !dns.IsPolling;
            int before = Volatile.Read(ref reads);
            dns.SetPolling(true);
            Check(firstRead && paused && dns.IsPolling && WaitFor(() => Volatile.Read(ref reads) > before, 2000),
                "DNS cache polling pauses, and resuming reads the cache immediately instead of waiting a full minute");
        }
        var timerless = new DnsCacheHints(() => Array.Empty<DnsCacheRecord>(), () => Environment.TickCount64, startTimer: true);
        ((System.Threading.Timer)typeof(DnsCacheHints).GetField("_timer", Private)!.GetValue(timerless)!).Dispose();
        timerless.SetPolling(false);
        timerless.SetPolling(true);
        timerless.Dispose();
        checks.Add("Pausing or resuming after the underlying timer was disposed is harmless");

        long heapBefore = GC.GetTotalMemory(false);
        var garbage = new byte[8 * 1024 * 1024];
        garbage[^1] = 1;
        garbage = null;
        IdleMemory.Trim();
        Check(GC.GetTotalMemory(false) <= heapBefore + 1024 * 1024, "The idle trim collects large unreachable buffers without throwing");

        string directory = Path.Combine(Path.GetTempPath(), "CodexIdlePolling", Guid.NewGuid().ToString("N"));
        try
        {
            using var engine = new MonitorEngine(directory);
            Check(engine.ViewerActive && engine.LivePollingActive && WaitFor(() => engine.MaintenancePasses >= 1, 3000),
                "A new engine has a viewer by default and maintains live data");
            engine.ViewerActive = false;
            Check(!engine.LivePollingActive, "A hidden, non-capturing engine pauses proxy and DNS polling");
            Thread.Sleep(1300); // let an in-flight pass finish
            long idle = engine.MaintenancePasses;
            Thread.Sleep(2300);
            Check(engine.MaintenancePasses == idle, "A hidden, non-capturing engine skips the per-second process, TCP and history refresh");
            engine.ViewerActive = true;
            Check(engine.LivePollingActive && WaitFor(() => engine.MaintenancePasses > idle, 900),
                "Showing the window resumes polling and refreshes immediately");

            int trims = 0;
            engine.IdleTrimDelay = TimeSpan.FromMilliseconds(300);
            engine.TrimMemory = () => Interlocked.Increment(ref trims);
            engine.ViewerActive = false;
            Check(WaitFor(() => Volatile.Read(ref trims) == 1, 2000), "Becoming idle and hidden trims memory once after a short delay");
            Thread.Sleep(700);
            Check(Volatile.Read(ref trims) == 1, "An idle engine does not keep trimming memory");
            engine.ViewerActive = true;
            engine.ViewerActive = false;
            engine.ViewerActive = true;
            Thread.Sleep(700);
            Check(Volatile.Read(ref trims) == 1, "Showing the window again before the delay cancels the pending trim");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
        return checks;
    }
}
