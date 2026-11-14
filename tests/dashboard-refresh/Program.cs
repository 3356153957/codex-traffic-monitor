using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTraffic;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        string data = Path.Combine(output, "fixture", Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        var now = DateTime.UtcNow;
        var rows = Enumerable.Range(0, 10000).Select(i => new FlowRecord
        {
            TimestampUtc = now, Application = i % 2 == 0 ? TrackedApplication.Codex : TrackedApplication.Claude,
            Process = i % 2 == 0 ? "codex.exe" : "claude.exe", Protocol = "TCP", LocalAddress = "127.0.0.1",
            LocalPort = 10000 + i, RemoteAddress = "203.0.113.1", RemotePort = 443,
            ProxyReportedHost = "target-" + i + ".example", UploadBytes = 1, DownloadBytes = 2
        }).ToList();
        using (var store = new SessionStore(data, true, true))
        { store.Append(rows); store.Update(10000, 20000, 0, ended: true, claudeUpload: 5000, claudeDownload: 10000); }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var engine = new MonitorEngine(data);
        MainWindow? window = null;
        object? benchmark = null;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (engine.GetDashboardSnapshot(DashboardRange.All).IsLoading && deadline.ElapsedMilliseconds < 15000) Thread.Sleep(50);
            Check(!engine.GetDashboardSnapshot(DashboardRange.All).IsLoading, "History loaded off the UI thread");
            window = new MainWindow(engine) { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();
            // Keep timer ticks under this test's control.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ((DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(window)!).Stop();
            var method = typeof(MainWindow).GetMethod("RefreshSnapshot", flags)!;
            void Refresh(bool tick) { method.Invoke(window, new object[] { tick }); window.UpdateLayout(); }
            T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Refresh(false);
            var grid = Find<DataGrid>("TargetsGrid");
            Check(grid.Items.Count == 10000, "Combined view retains all 10,000 destinations");
            object initialItems = grid.ItemsSource;
            Refresh(true);
            Check(ReferenceEquals(initialItems, grid.ItemsSource), "An unchanged visible tick retains its bound destination list");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Refresh(true);
            timer.Stop();
            long idleAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var forcedTimer = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) Refresh(false);
            forcedTimer.Stop();
            long forcedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            benchmark = new { Targets = 10000, IdleAllocatedBytesPerTick = idleAllocated / 20,
                ForcedAllocatedBytesPerRefresh = forcedAllocated / 5, IdleMillisecondsPerTick = timer.Elapsed.TotalMilliseconds / 20,
                ForcedMillisecondsPerRefresh = forcedTimer.Elapsed.TotalMilliseconds / 5 };
            Check(idleAllocated / 20 < forcedAllocated / 5 / 5, "Idle destination refresh allocation is less than one fifth of a rebuild");
            Click("ApplicationCodexButton");
            Check(grid.Items.Count == 5000, "Application filtering invalidates the cached list");
            object applicationSnapshot = typeof(MainWindow).GetField("_dashboard", flags)!.GetValue(window)!;
            object rankItems = Find<ItemsControl>("RankingList").ItemsSource;
            object processItems = Find<DataGrid>("ProcessesGrid").ItemsSource;
            object sessionItems = Find<DataGrid>("SessionsGrid").ItemsSource;
            Find<TextBox>("TargetSearchBox").Text = "target-100.example";
            Check(grid.Items.Count == 1, "Search invalidates cached destination bindings");
            Check(ReferenceEquals(applicationSnapshot, typeof(MainWindow).GetField("_dashboard", flags)!.GetValue(window)), "Search reuses the detached historical data instead of cloning all destinations");
            Check(ReferenceEquals(rankItems, Find<ItemsControl>("RankingList").ItemsSource) &&
                ReferenceEquals(processItems, Find<DataGrid>("ProcessesGrid").ItemsSource) &&
                ReferenceEquals(sessionItems, Find<DataGrid>("SessionsGrid").ItemsSource),
                "Typing a destination search preserves rankings, process and session views");
            Find<TextBox>("TargetSearchBox").Text = "TARGET-100.EXAMPLE";
            Check(grid.Items.Count == 1, "Destination search remains case insensitive");
            Find<TextBox>("TargetSearchBox").Text = "203.0.113.1";
            Check(grid.Items.Count == 5000, "IP search retains the selected application's complete matching history");
            Find<TextBox>("TargetSearchBox").Text = "missing-destination.invalid";
            Check(grid.Items.Count == 0, "An unmatched query empties only the destination list");
            Click("ClearSearchButton");
            Check(grid.Items.Count == 5000, "Clearing search restores all selected application targets");
            Check(ReferenceEquals(rankItems, Find<ItemsControl>("RankingList").ItemsSource),
                "Clearing the search does not rebuild rankings");
            var match = typeof(MainWindow).GetMethod("TargetMatches", BindingFlags.Static | BindingFlags.NonPublic)!;
            var searchable = new TargetRow { Application = TrackedApplication.Claude, Hostname = "host.example",
                Address = "203.0.113.4", Target = "target-token", ProxyDestination = "destination.example:443",
                Process = "worker.exe", Region = "日本", ProxyNode = "节点甲", ProxyChain = "策略乙 → 节点甲",
                ProxyType = "Shadowsocks", TrafficKind = TrafficKind.Proxy };
            Check(new[] { "Claude", "HOST.EXAMPLE", "203.0.113.4", "target-token", "destination.example", "worker.exe",
                "日本", "节点甲", "策略乙", "Shadowsocks", "代理", ConnectionPresentation.RegionLabel(searchable),
                ConnectionPresentation.NodeLabel(searchable), ConnectionPresentation.NodeContext(searchable) }
                .All(query => (bool)match.Invoke(null, new object[] { searchable, query })!),
                "Search retains all fourteen application, endpoint, process, region and proxy display fields");
            Click("ApplicationClaudeButton");
            Check(grid.Items.Count == 5000, "Claude scope still shows its own 5,000 targets");
            Click("TrafficProxyButton");
            Check(grid.Items.Count == 0, "Proxy-only scope invalidates the cached list conservatively");
            Click("TrafficAllButton");
            Click("RangeTodayButton");
            Check(grid.Items.Count == 5000, "Date filtering preserves current application and invalidates its bindings");
            object beforeAppend = grid.ItemsSource;
            var versionProperty = typeof(MonitorEngine).GetProperty("DashboardVersion", flags)!;
            object beforeVersion = versionProperty.GetValue(engine)!;
            using (var additional = new SessionStore(data, true, true))
            {
                additional.Append(new[] { new FlowRecord { TimestampUtc = now, Application = TrackedApplication.Claude,
                    Process = "claude.exe", Protocol = "TCP", RemoteAddress = "203.0.113.2", RemotePort = 443, UploadBytes = 3 } });
                additional.Update(3, 0, 0, ended: true, claudeUpload: 3);
            }
            deadline.Restart();
            while (ReferenceEquals(beforeVersion, versionProperty.GetValue(engine)) && deadline.ElapsedMilliseconds < 10000) Thread.Sleep(50);
            Refresh(true);
            Check(grid.Items.Count == 5001 && !ReferenceEquals(beforeAppend, grid.ItemsSource), "New persisted traffic invalidates a visible periodic tick without manual refresh");
            Click("SettingsButton");
            Thread.Sleep(100);
            Refresh(true);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var popup = Find<Popup>("SettingsPopup");
            Check(popup.IsOpen && Find<TextBlock>("ResourceStatusText").Text.Contains("CPU"), "Settings shows live CPU and memory");
            Check(Find<Button>("ExportDiagnosticsButton").IsVisible, "Diagnostics export remains visible in settings");
            var child = (FrameworkElement)popup.Child;
            child.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(child);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var image = File.Create(Path.Combine(output, "settings.png"))) encoder.Save(image);
            popup.IsOpen = false;
            grid.SelectedIndex = 20;
            string selected = (string)grid.SelectedItem.GetType().GetProperty("Key")!.GetValue(grid.SelectedItem)!;
            var scroller = FindScroller(grid);
            scroller?.ScrollToVerticalOffset(18);
            window.UpdateLayout();
            double offset = scroller?.VerticalOffset ?? 0;
            window.Hide();
            var savedOffsets = (Dictionary<string, double>)typeof(MainWindow).GetField("_suspendedScrollOffsets", flags)!.GetValue(window)!;
            double savedOffset = savedOffsets.GetValueOrDefault("TargetsGrid");
            Check(grid.ItemsSource == null && typeof(MainWindow).GetField("_dashboard", flags)!.GetValue(window) == null &&
                typeof(MainWindow).GetField("_dashboardVersion", flags)!.GetValue(window) == null,
                "Hiding drops full historical rows, WPF bindings and the old publication reference");
            Refresh(true);
            Check(grid.ItemsSource == null, "A hidden tick does not rebuild discarded dashboard bindings");
            Refresh(false);
            Check(grid.ItemsSource == null && typeof(MainWindow).GetField("_dashboard", flags)!.GetValue(window) == null,
                "Explicit refresh while hidden does not rebuild the dashboard (including tray monitoring actions)");
            window.Show();
            Refresh(false);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(grid.Items.Count == 5001 && grid.SelectedItem != null &&
                (string)grid.SelectedItem.GetType().GetProperty("Key")!.GetValue(grid.SelectedItem)! == selected,
                "Restoring rebuilds the selected application's complete list and restores the selected destination");
            Check(Math.Abs((FindScroller(grid)?.VerticalOffset ?? 0) - offset) <= 1,
                $"Restoring preserves the destination table's scroll position (before={offset}, saved={savedOffset}, after={FindScroller(grid)?.VerticalOffset})");
            var report = ResourceDiagnostics.MeasureAsync(Environment.ProcessId, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            Check(report.Samples.Count >= 2 && report.PeakWorkingSetBytes > 0, "Live diagnostics sampling produces a usable resource report");
            File.WriteAllText(Path.Combine(output, "ui-checks.json"), JsonSerializer.Serialize(new { Success = true, checks, benchmark }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "ui-checks.json"), JsonSerializer.Serialize(new { Success = false, checks, benchmark, Error = exception.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { window?.RequestExit(); app.Shutdown(); }
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found) return found;
            if (FindScroller(child) is ScrollViewer nested) return nested;
        }
        return null;
    }
}
