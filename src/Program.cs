using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace CodexTraffic;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) return SelfTests.Run(ValueAfter(args, "--self-test"));
            // Installer helpers run before the normal UI's global instance lock.
            if (args.Contains("--maintenance"))
                return InstallerIntegration.Run(ValueAfter(args, "--maintenance"), ValueAfter(args, "--install-path"), ValueAfter(args, "--state"));
            if (args.Contains("--configure-startup"))
            {
                string? option = ValueAfter(args, "--configure-startup");
                if (option is not ("on" or "off")) throw new ArgumentException("--configure-startup 需要 on 或 off。");
                return StartupManager.ConfigureCommand(option == "on", ValueAfter(args, "--request"), ValueAfter(args, "--owner-sid"), ValueAfter(args, "--expected-executable"));
            }
            if (args.Contains("--startup-status")) { WriteJson(ValueAfter(args, "--startup-status") ?? "startup-status.json", StartupManager.ReadStatus()); return 0; }
            if (args.Contains("--dashboard-snapshot"))
            {
                var history = new HistoryAnalytics(StartupManager.DataDirectory);
                history.Refresh();
                var range = ValueAfter(args, "--range") switch { "today" => DashboardRange.Today, "7d" => DashboardRange.Last7Days, "30d" => DashboardRange.Last30Days, _ => DashboardRange.All };
                var trafficScope = ValueAfter(args, "--traffic") == "proxy" ? DashboardTrafficScope.Proxy : DashboardTrafficScope.All;
                var dashboard = history.GetSnapshot(range, trafficScope);
                using var geo = new GeoIpHints();
                foreach (var target in dashboard.Targets) geo.Enrich(target);
                WriteJson(ValueAfter(args, "--dashboard-snapshot") ?? "dashboard.json", dashboard);
                return 0;
            }
            if (args.Contains("--snapshot"))
            {
                using var engine = new MonitorEngine();
                Thread.Sleep(1800);
                WriteJson(ValueAfter(args, "--snapshot") ?? "snapshot.json", engine.GetSnapshot());
                return 0;
            }
            if (args.Contains("--capture-probe")) return CaptureProbe(args);
            bool preview = args.Contains("--preview"), livePreview = args.Contains("--preview-live");
            bool anyPreview = preview || livePreview;
            if (int.TryParse(ValueAfter(args, "--handoff-from"), out int previousPid))
            {
                try { using var previous = Process.GetProcessById(previousPid); previous.WaitForExit(10000); }
                catch (ArgumentException) { }
            }
            using var instance = anyPreview ? null : SingleInstance.TryCreate();
            if (!anyPreview && instance == null)
            {
                // A task launch must leave the existing background window hidden.
                if (!args.Contains("--minimized")) SingleInstance.ShowExisting();
                return 0;
            }
            using var monitor = new MonitorEngine();
            var options = anyPreview ? new CaptureOptions() : StartupManager.LoadCaptureOptions();
            monitor.IncludeChildren = args.Contains("--include-children") || options.IncludeChildren;
            monitor.IncludeLoopback = !args.Contains("--exclude-loopback") && options.IncludeLoopback;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) =>
            {
                monitor.Stop();
                MessageBox.Show("程序遇到错误，采集已停止。\n" + e.Exception.Message, "Codex 流量观察", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            if (livePreview) Thread.Sleep(2200);
            var window = new MainWindow(monitor, preview) { StartHidden = args.Contains("--minimized") && !anyPreview };
            instance?.Attach(window);
            if (preview || livePreview) window.PreviewOutputPath = Path.GetFullPath(ValueAfter(args, livePreview ? "--preview-live" : "--preview") ?? "preview.png");
            if (args.Contains("--start") && !preview) window.Loaded += (_, _) => monitor.Start();
            return app.Run(window);
        }
        catch (Exception e)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTrafficMonitor");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "errors.log"), DateTime.Now.ToString("O") + " " + e + Environment.NewLine);
            if (!args.Any(x => x.StartsWith("--self-test") || x.StartsWith("--capture-probe") || x.StartsWith("--snapshot") || x.StartsWith("--configure-startup") || x.StartsWith("--startup-status") || x.StartsWith("--dashboard-snapshot") || x.StartsWith("--maintenance")))
                MessageBox.Show(e.Message, "Codex 流量观察", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    public static bool RelaunchElevated(bool includeChildren = false, bool includeLoopback = true)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = "--start --handoff-from " + Environment.ProcessId + (includeChildren ? " --include-children" : "") + (!includeLoopback ? " --exclude-loopback" : ""),
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; }
    }

    private static int CaptureProbe(string[] args)
    {
        string output = Path.GetFullPath(ValueAfter(args, "--capture-probe") ?? "capture-probe.json");
        string data = Path.Combine(Path.GetDirectoryName(output)!, "probe-data");
        using var engine = new MonitorEngine(data);
        if (!engine.IsAdmin)
        {
            WriteJson(output, new { success = false, reason = "Administrator privileges are required for live ETW capture." });
            return 2;
        }
        engine.Start();
        int seconds = int.TryParse(ValueAfter(args, "--seconds"), out var n) ? Math.Clamp(n, 5, 120) : 20;
        for (int i = 0; i < seconds; i++) Thread.Sleep(1000);
        engine.Stop();
        engine.Dispose();
        var snapshot = engine.GetSnapshot();
        engine.ExportCsv(Path.ChangeExtension(output, ".csv"));
        WriteJson(output, new { success = snapshot.HasCapturedData && engine.LastError.Length == 0 && snapshot.UploadBytes + snapshot.DownloadBytes > 0, error = engine.LastError, snapshot });
        return engine.LastError.Length == 0 && snapshot.UploadBytes + snapshot.DownloadBytes > 0 ? 0 : 3;
    }

    private static string? ValueAfter(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
