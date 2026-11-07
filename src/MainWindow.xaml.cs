using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CodexTraffic;

public partial class MainWindow : Window
{
    private readonly MonitorEngine _engine;
    private readonly bool _demo;
    private readonly DispatcherTimer _timer;
    private MonitorSnapshot? _snapshot;
    private DashboardSnapshot? _dashboard;
    private DashboardRange _range = DashboardRange.All;
    private DashboardTrafficScope _trafficScope = DashboardTrafficScope.All;
    private DashboardApplicationScope _applicationScope = DashboardApplicationScope.Total;
    private readonly CancellationTokenSource _updateCancellation = new();
    private UpdateCheckResult? _availableUpdate;
    private bool _checkingUpdate;
    private readonly Dictionary<string, (string Property, ListSortDirection Direction)> _sorts = new()
    {
        ["TargetsGrid"] = ("TotalBytes", ListSortDirection.Descending),
        ["ProcessesGrid"] = ("Pid", ListSortDirection.Ascending),
        ["SessionsGrid"] = ("StartedAt", ListSortDirection.Descending)
    };
    private bool _loading = true;
    private bool _windowLoaded;
    private bool _allowExit;
    private bool _disposed;
    private bool _startupChanging;
    private bool _startupRefreshing;
    private bool _updatingStartupCheck;
    private bool _startupEnabled;
    private bool _compactLayout;
    private bool _refreshingTargets;
    private string? _selectedTargetKey;
    private int _targetTotalCount;
    private TrayController? _tray;
    private string _selectedTab = "targets";
    public string? PreviewOutputPath { get; set; }
    public bool StartHidden { get; set; }

    public MainWindow(MonitorEngine engine, bool demo = false)
    {
        _engine = engine;
        _demo = demo;
        InitializeComponent();
        CurrentVersionText.Text = "当前 v" + UpdateChecker.CurrentVersion;
        if (demo)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
        }
        ChildrenCheck.IsChecked = engine.IncludeChildren;
        LoopbackCheck.IsChecked = engine.IncludeLoopback;
        _loading = false;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshSnapshot(true);
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        StateChanged += (_, _) =>
        {
            if (!_demo && string.IsNullOrWhiteSpace(PreviewOutputPath) && WindowState == WindowState.Minimized) HideToTray();
        };
        Activated += async (_, _) =>
        {
            if (_windowLoaded && !_demo && string.IsNullOrWhiteSpace(PreviewOutputPath)) await RefreshStartupStatusAsync();
        };
        Closed += (_, _) => Cleanup();
        Application.Current.SessionEnding += Application_SessionEnding;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshSnapshot(false);
        if (!_demo) _timer.Start();
        _windowLoaded = true;
        if (!_demo && string.IsNullOrWhiteSpace(PreviewOutputPath))
        {
            _tray = new TrayController(Dispatcher, RestoreFromTray, ToggleMonitoring, RequestExit);
            _tray.UpdateMonitoring(_engine.IsRunning);
            if (StartHidden) HideToTray(false);
            await RefreshStartupStatusAsync();
        }
        if (!string.IsNullOrWhiteSpace(PreviewOutputPath))
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try { SavePreview(PreviewOutputPath!); }
                catch (Exception exception) { Trace.WriteLine("Preview: " + exception.Message); }
                finally { Application.Current.Shutdown(); }
            }));
        }
    }

    private void RefreshSnapshot(bool addSample)
    {
        if (_disposed) return;
        // Collection and disk flushes run in MonitorEngine independently. A tray
        // window only needs its icon state, not thousands of data-bound rows.
        if (addSample && !IsVisible && !_demo)
        {
            _tray?.UpdateMonitoring(_engine.IsRunning);
            return;
        }
        try
        {
            _snapshot = _demo ? CreateDemoSnapshot(_applicationScope) : _engine.GetSnapshot(includeTargets: false, applicationScope: _applicationScope);
            _dashboard = _demo ? CreateDemoDashboard(_range, _trafficScope, _applicationScope) : _engine.GetDashboardSnapshot(_range, _trafficScope, _applicationScope);
            var current = _snapshot;
            var summary = _dashboard;
            UpdateRangeButtons();
            UpdateTrafficScopeButtons();
            UpdateApplicationScopeButtons();
            bool running = _demo || _engine.IsRunning;
            bool waiting = summary.IsLoading;
            long total = summary.UploadBytes + summary.DownloadBytes;
            ExportButton.IsEnabled = !_demo && summary.HasRecords && !waiting;
            ExportButton.Content = "导出" + TrafficScopeLabel(_trafficScope) + " CSV";
            ExportButton.ToolTip = ApplicationScopeLabel(_applicationScope) + " · " + RangeLabel(_range) + " · " + TrafficScopeLabel(_trafficScope) + " · 导出完整去向，不受搜索文字影响";
            TotalTitleText.Text = ApplicationScopeLabel(_applicationScope) + " · " + (_trafficScope == DashboardTrafficScope.Proxy ? (_range == DashboardRange.All ? "累计代理流量" : RangeLabel(_range) + "代理流量") : _range == DashboardRange.All ? "累计总流量" : RangeLabel(_range) + "总流量");
            TotalText.Text = waiting ? "…" : Bytes(total);
            UploadTotalText.Text = waiting ? "…" : Bytes(summary.UploadBytes);
            DownloadTotalText.Text = waiting ? "…" : Bytes(summary.DownloadBytes);
            long allTotal = summary.AllUploadBytes + summary.AllDownloadBytes;
            long proxyTotal = summary.ProxyUploadBytes + summary.ProxyDownloadBytes;
            double proxyFraction = allTotal > 0 ? Math.Clamp(proxyTotal / (double)allTotal, 0, 1) : 0;
            ComparisonTitleText.Text = _trafficScope == DashboardTrafficScope.All ? "其中 · 已确认代理" : "同一时间范围 · 全部流量";
            ComparisonTotalText.Text = waiting ? "…" : Bytes(_trafficScope == DashboardTrafficScope.All ? proxyTotal : allTotal);
            ProxyShareText.Text = waiting ? "正在汇总记录…" : "代理占全部 " + (proxyFraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            ProxyFractionColumn.Width = new GridLength(proxyFraction, GridUnitType.Star);
            RemainingFractionColumn.Width = new GridLength(1 - proxyFraction, GridUnitType.Star);
            TrafficComparisonCard.ToolTip = "所选时间范围 · 全部 " + Bytes(allTotal) + "\n已确认代理 " + Bytes(proxyTotal) + "\n已确认直连 " + Bytes(summary.DirectUploadBytes + summary.DirectDownloadBytes) + "\n未知 " + Bytes(summary.UnknownUploadBytes + summary.UnknownDownloadBytes) + "\n未知表示未取得足够代理报告，不等于直连。";
            UploadShareText.Text = total > 0 ? "占所选范围 " + (summary.UploadBytes * 100d / total).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "发送至目标的流量";
            DownloadShareText.Text = total > 0 ? "占所选范围 " + (summary.DownloadBytes * 100d / total).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "从目标接收的流量";
            TotalHintText.Text = waiting ? "正在读取本机历史记录" : "今日 " + Bytes(summary.TodayUploadBytes + summary.TodayDownloadBytes) + " · 已记录 " + summary.RecordedDays + " 天";
            TotalHintText.ToolTip = ApplicationScopeLabel(_applicationScope) + " · " + TrafficScopeLabel(_trafficScope) + " · " + (summary.FirstRecordedUtc.HasValue ? "自 " + summary.FirstRecordedUtc.Value.ToLocalTime().ToString("yyyy/MM/dd") + " 开始记录" : "开始监控后持续累计，重启保留记录") + "\n" + summary.SessionCount + " 次监控 · 仅包含已监控时段，不代表全天覆盖。";
            ScopeText.Text = waiting ? "正在汇总应用用量…" : "Codex " + Bytes(summary.CodexUploadBytes + summary.CodexDownloadBytes) + " · Claude " + Bytes(summary.ClaudeUploadBytes + summary.ClaudeDownloadBytes);
            ScopeText.ToolTip = RangeLabel(_range) + " · " + TrafficScopeLabel(_trafficScope) + " · " + (summary.FromUtc.HasValue ? summary.FromUtc.Value.ToLocalTime().ToString("MM/dd") + " – " + summary.ToUtc.ToLocalTime().ToString("MM/dd") : "从首次记录至今") + "\nCodex 上行 " + Bytes(summary.CodexUploadBytes) + " / 下行 " + Bytes(summary.CodexDownloadBytes) + "\nClaude 上行 " + Bytes(summary.ClaudeUploadBytes) + " / 下行 " + Bytes(summary.ClaudeDownloadBytes);
            UploadRateText.Text = current.HasCapturedData ? "↑ " + Bytes(current.UploadBytesPerSecond) + "/s" : "↑ 尚未采集";
            DownloadRateText.Text = current.HasCapturedData ? "↓ " + Bytes(current.DownloadBytesPerSecond) + "/s" : "↓ 尚未采集";
            RealtimeScopeText.Text = "实时 · " + ApplicationScopeLabel(_applicationScope) + " · 全部流量";
            ProcessHintText.Text = current.ProcessCount + " 个进程 · " + current.ConnectionCount + " 个连接";
            OptionsSummaryText.Text = (ChildrenCheck.IsChecked == true ? "派生工具已计入" : "仅核心进程") + (_startupEnabled ? " · 自动启动已开启" : "");
            SettingsHintText.Text = running ? "暂停监控后可修改采集范围，已保存的记录不受影响。" : "选择会保存，并用于下次自动启动。";

            DateTime chartLast = summary.ToUtc == default ? DateTime.Today : summary.ToUtc.ToLocalTime().Date;
            DateTime chartFirst = _range == DashboardRange.Today ? chartLast : _range == DashboardRange.Last7Days ? chartLast.AddDays(-6) : chartLast.AddDays(-29);
            if (_range == DashboardRange.All && summary.FirstRecordedUtc.HasValue)
            {
                var earliest = summary.FirstRecordedUtc.Value.ToLocalTime().Date;
                if (earliest > chartFirst) chartFirst = earliest < chartLast.AddDays(-6) ? earliest : chartLast.AddDays(-6);
            }
            int chartDays = (int)(chartLast - chartFirst).TotalDays + 1;
            DailyRangeText.Text = ApplicationScopeLabel(_applicationScope) + " · " + TrafficScopeLabel(_trafficScope) + " · " + (_range == DashboardRange.Today ? "今日" : "最近 " + chartDays + " 天") + (_range == DashboardRange.All ? "趋势 · 累计包含全部日期" : " · 空白日期表示当天无记录");
            DailyRangeText.ToolTip = "每日上下行按本地日期汇总，只包含实际监控时段。" + (_range == DashboardRange.All ? "图表显示最近日期，顶部累计用量包含全部历史记录。" : "");
            DailyChart.SetUsage(summary.DailyUsage, chartFirst, chartLast, waiting);

            var sortedTargets = summary.Targets.OrderByDescending(row => row.UploadBytes + row.DownloadBytes).ThenBy(row => row.Hostname).ThenBy(row => row.Address).ToList();
            _targetTotalCount = sortedTargets.Count;
            string search = TargetSearchBox.Text.Trim();
            var targets = sortedTargets.Where(row => search.Length == 0 || TargetMatches(row, search))
                .Select(row => new TargetDisplayRow { Source = row }).ToList();
            _refreshingTargets = true;
            try
            {
                SetGridItems(TargetsGrid, targets);
                TargetsGrid.SelectedItem = _selectedTargetKey == null ? null : targets.FirstOrDefault(row => row.Key == _selectedTargetKey);
            }
            finally { _refreshingTargets = false; }
            UpdateTargetDetails();
            SetGridItems(ProcessesGrid, current.Processes.Select(row => new ProcessDisplayRow
            {
                Name = row.Name, Pid = row.Pid, Kind = row.Application + (row.Kind == "tool" ? " 派生工具" : " 核心进程"),
                UploadBytes = row.UploadBytes, DownloadBytes = row.DownloadBytes,
                UploadDisplay = current.HasCapturedData ? Bytes(row.UploadBytes) : "—", DownloadDisplay = current.HasCapturedData ? Bytes(row.DownloadBytes) : "—"
            }).ToList());
            SetGridItems(SessionsGrid, current.Sessions.Where(row => SessionInRange(row, summary)).Select(row => new SessionDisplayRow
            {
                StartedAt = row.StartedAt, Duration = row.Duration, UploadBytes = row.UploadBytes, DownloadBytes = row.DownloadBytes,
                UploadDisplay = Bytes(row.UploadBytes), DownloadDisplay = Bytes(row.DownloadBytes), TotalDisplay = Bytes(row.UploadBytes + row.DownloadBytes)
            }).ToList());
            var destinations = sortedTargets.GroupBy(TargetName)
                .Select(group => new { Name = group.Key, Bytes = group.Sum(row => row.UploadBytes + row.DownloadBytes), Rows = group })
                .OrderByDescending(row => row.Bytes).Take(5)
                .Select(row => new { row.Name, row.Bytes, Details = string.Join("\n", row.Rows.Select(target => FormatAddress(target.Address, target.Port)).Distinct().Take(8)) }).ToList();
            double rankMaximum = destinations.Count == 0 ? 1 : Math.Max(1, destinations[0].Bytes);
            RankingList.ItemsSource = destinations.Select((row, index) => new RankingDisplayRow { Rank = (index + 1).ToString(), Name = row.Name, Details = row.Details + "\n占当前范围 " + (total > 0 ? row.Bytes * 100d / total : 0).ToString("0.0") + "% · 点击筛选", Display = Bytes(row.Bytes), UsedWidth = new GridLength(Math.Max(.001, row.Bytes / rankMaximum), GridUnitType.Star), RemainingWidth = new GridLength(Math.Max(.001, 1 - row.Bytes / rankMaximum), GridUnitType.Star) }).ToList();
            RankingEmptyText.Visibility = destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RankingEmptyText.Text = waiting ? "正在汇总历史用量…" : _trafficScope == DashboardTrafficScope.Proxy ? "当前范围暂无已确认代理流量" : "当前范围暂无流量记录";

            StatusText.Text = _demo ? "演示预览" : current.CaptureStatus;
            Color active = Color.FromRgb(22, 143, 106);
            StatusDot.Fill = new SolidColorBrush(running ? active : Color.FromRgb(148, 163, 184));
            StatusText.Foreground = new SolidColorBrush(running ? active : Color.FromRgb(101, 116, 134));
            StatusPill.Background = new SolidColorBrush(running ? Color.FromRgb(234, 247, 240) : Color.FromRgb(234, 240, 245));
            StartButtonText.Text = running ? "暂停监控" : _engine.IsAdmin || _demo ? "开始监控" : "以管理员身份开始";
            _tray?.UpdateMonitoring(running);
            StartIcon.Data = Geometry.Parse(running ? "M 0,0 L 3,0 L 3,10 L 0,10 Z M 6,0 L 9,0 L 9,10 L 6,10 Z" : "M 0,0 L 9,5 L 0,10 Z");
            AdminButton.Visibility = _engine.IsAdmin || _demo ? Visibility.Collapsed : Visibility.Visible;
            ChildrenCheck.IsEnabled = !running && !_demo;
            LoopbackCheck.IsEnabled = !running && !_demo;
            SessionTimeText.Text = current.StartedAt.HasValue ? "本次 " + ApplicationScopeLabel(_applicationScope) + " " + Bytes(current.UploadBytes + current.DownloadBytes) : "本次尚未开始";
            SessionTimeText.ToolTip = current.StartedAt.HasValue ? "本次监控自 " + current.StartedAt.Value.ToString("HH:mm:ss") + " 开始，顶部统计包含所选范围的历史记录。" : "点击开始监控，记录将自动保存在本机。";
            WarningText.Text = "未知未取得足够代理报告，不等于直连；仅含已监控时段与 socket 流量。";
            WarningText.ToolTip = string.Join("\n", new[] { WarningText.Text, summary.Warning, current.Warning }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
            if (current.EventsLost > 0) WarningText.Text += " 丢失事件 " + current.EventsLost + " 个。";
            string issue = !string.IsNullOrWhiteSpace(_engine.LastError) ? _engine.LastError : !string.IsNullOrWhiteSpace(summary.Warning) ? summary.Warning : current.EventsLost > 0 ? current.Warning : "";
            CaptureHintText.Text = !string.IsNullOrWhiteSpace(issue) ? issue : _demo ? "演示数据 · 仅用于界面预览" : waiting ? "正在汇总本机历史记录…" : running ? "正在记录 · 重启后继续累计" : summary.HasRecords ? "历史记录已保留 · 监控未运行" : "尚无历史记录 · 开始监控后累计";
            CaptureHintText.ToolTip = CaptureHintText.Text;
            CaptureDot.Fill = new SolidColorBrush(!string.IsNullOrWhiteSpace(issue) ? Color.FromRgb(185, 131, 60) : running ? active : Color.FromRgb(154, 167, 181));
            UpdateEmptyState();
        }
        catch (Exception exception)
        {
            CaptureHintText.Text = "刷新失败：" + exception.Message;
            CaptureHintText.ToolTip = exception.ToString();
        }
    }

    private void UpdateResponsiveLayout()
    {
        bool compact = ActualHeight < 900;
        if (compact == _compactLayout) return;
        _compactLayout = compact;
        var content = (Grid)Content;
        content.Margin = compact ? new Thickness(28, 16, 28, 10) : new Thickness(28, 20, 28, 14);
        content.RowDefinitions[0].Height = new GridLength(compact ? 60 : 64);
        content.RowDefinitions[1].Height = new GridLength(compact ? 38 : 42);
        content.RowDefinitions[3].Height = new GridLength(compact ? 170 : 212);
        content.RowDefinitions[4].Height = new GridLength(compact ? 28 : 32);
        DailyChart.MinHeight = compact ? 90 : 110;
        Resources["RankingRowMargin"] = new Thickness(0, compact ? 0 : 3, 0, compact ? 0 : 3);
    }

    private static string RangeLabel(DashboardRange range) => range switch { DashboardRange.Today => "今日", DashboardRange.Last7Days => "近 7 天", DashboardRange.Last30Days => "近 30 天", _ => "全部记录" };
    private static string TrafficScopeLabel(DashboardTrafficScope scope) => scope == DashboardTrafficScope.Proxy ? "已确认代理" : "全部流量";
    private static string ApplicationScopeLabel(DashboardApplicationScope scope) => scope switch { DashboardApplicationScope.Codex => "Codex", DashboardApplicationScope.Claude => "Claude", _ => "合计" };
    private static string TrafficKindLabel(TrafficKind kind) => kind switch { TrafficKind.Proxy => "已确认代理", TrafficKind.Direct => "已确认直连", _ => "未知，未取得足够代理报告，不等于直连" };
    private static string ShortTrafficKindLabel(TrafficKind kind) => kind switch { TrafficKind.Proxy => "代理", TrafficKind.Direct => "直连", _ => "未知" };
    private static string TargetName(TargetRow row) => !string.IsNullOrWhiteSpace(row.Hostname) ? row.Hostname : !string.IsNullOrWhiteSpace(row.Target) ? row.Target : row.Address;
    private static string TargetIdentity(TargetRow row) => string.Join("\u001f", row.Application, row.Process.ToLowerInvariant(), row.Protocol.ToUpperInvariant(), row.Address.ToLowerInvariant(), row.Port, row.Hostname.ToLowerInvariant(), row.ProxyDestination.ToLowerInvariant(), row.ProxyNode, row.ProxyType.ToLowerInvariant(), row.TrafficKind);
    private static string DisplayRoute(TargetRow row)
    {
        string route = row.Route;
        if (route.Contains("回环", StringComparison.Ordinal)) return "本机回环";
        if (route.Contains("虚拟", StringComparison.Ordinal) || route.Contains("TUN", StringComparison.OrdinalIgnoreCase)) return "虚拟地址";
        if (route.Contains("公网", StringComparison.Ordinal)) return "公网";
        if (route.Contains("私有", StringComparison.Ordinal) || route.Contains("内网", StringComparison.Ordinal)) return "内网";
        if (route.Contains("保留", StringComparison.Ordinal)) return "保留地址";
        return route;
    }
    private static bool TargetMatches(TargetRow row, string search) => new[] { row.Application.ToString(), row.Hostname, row.Address, row.Target, row.ProxyDestination, row.Process, row.Region, row.ProxyNode, row.ProxyChain, row.ProxyType, TrafficKindLabel(row.TrafficKind), ConnectionPresentation.RegionLabel(row), ConnectionPresentation.NodeLabel(row), ConnectionPresentation.NodeContext(row) }.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    private static bool SessionInRange(SessionRow row, DashboardSnapshot summary) => !summary.FromUtc.HasValue || (DateTime.TryParse(row.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var started) && started.ToUniversalTime() >= summary.FromUtc.Value && started.ToUniversalTime() <= summary.ToUtc);

    private void SetGridItems(DataGrid grid, System.Collections.IEnumerable rows)
    {
        var scroller = FindVisualChild<ScrollViewer>(grid);
        double offset = scroller?.VerticalOffset ?? 0;
        var view = CollectionViewSource.GetDefaultView(rows);
        var sort = _sorts[grid.Name];
        grid.ItemsSource = view;
        view.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
        foreach (var column in grid.Columns) column.SortDirection = column.SortMemberPath == sort.Property ? sort.Direction : null;
        if (offset > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_disposed) scroller?.ScrollToVerticalOffset(offset); }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is T nested) return nested;
        }
        return null;
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_loading || sender is not DataGrid grid || string.IsNullOrWhiteSpace(e.Column.SortMemberPath)) return;
        e.Handled = true;
        var before = _sorts[grid.Name];
        var direction = before.Property == e.Column.SortMemberPath && before.Direction == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        _sorts[grid.Name] = (e.Column.SortMemberPath, direction);
        foreach (var column in grid.Columns) column.SortDirection = column == e.Column ? direction : null;
        RefreshSnapshot(false);
    }

    private void RangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _range = RangeCombo.SelectedIndex switch { 0 => DashboardRange.Today, 1 => DashboardRange.Last7Days, 2 => DashboardRange.Last30Days, _ => DashboardRange.All };
        RefreshSnapshot(false);
    }

    private void RangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int index)) RangeCombo.SelectedIndex = index;
    }

    private void TrafficScopeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button button) return;
        var next = button.Tag?.ToString() == "Proxy" ? DashboardTrafficScope.Proxy : DashboardTrafficScope.All;
        if (next == _trafficScope) return;
        _trafficScope = next;
        RefreshSnapshot(false);
    }

    private void ApplicationScopeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button button || !Enum.TryParse<DashboardApplicationScope>(button.Tag?.ToString(), out var next) || next == _applicationScope) return;
        _applicationScope = next;
        _selectedTargetKey = null;
        RefreshSnapshot(false);
    }

    private void UpdateApplicationScopeButtons()
    {
        foreach (var button in new[] { ApplicationTotalButton, ApplicationCodexButton, ApplicationClaudeButton })
        {
            bool selected = button.Tag?.ToString() == _applicationScope.ToString();
            button.Background = selected ? Brushes.White : Brushes.Transparent;
            button.Foreground = new SolidColorBrush(selected ? Color.FromRgb(27, 48, 64) : Color.FromRgb(102, 120, 136));
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "当前应用范围" : "切换应用范围；时间与代理筛选保持不变");
        }
    }

    private void UpdateTrafficScopeButtons()
    {
        foreach (var button in new[] { TrafficAllButton, TrafficProxyButton })
        {
            bool selected = button.Tag?.ToString() == _trafficScope.ToString();
            button.Background = selected ? Brushes.White : Brushes.Transparent;
            button.Foreground = new SolidColorBrush(selected ? Color.FromRgb(27, 48, 64) : Color.FromRgb(102, 120, 136));
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "当前流量范围" : "切换流量范围");
        }
    }

    private void UpdateRangeButtons()
    {
        foreach (var button in new[] { RangeAllButton, RangeTodayButton, RangeWeekButton, RangeMonthButton })
        {
            bool selected = button.Tag?.ToString() == RangeCombo.SelectedIndex.ToString();
            button.Background = selected ? Brushes.White : Brushes.Transparent;
            button.Foreground = new SolidColorBrush(selected ? Color.FromRgb(27, 48, 64) : Color.FromRgb(102, 120, 136));
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "当前统计范围" : "切换统计范围");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate || _disposed) return;
        if (_demo) { UpdateStatusText.Text = "演示预览 · 实际检测会读取 GitHub Releases 的正式版本"; return; }
        SetUpdateCheckState(true);
        try
        {
            var result = await UpdateChecker.CheckAsync(_updateCancellation.Token);
            if (_disposed) return;
            SetUpdateCheckState(false, result);
        }
        catch (OperationCanceledException) { if (!_disposed) SetUpdateCheckState(false, new() { Status = UpdateCheckStatus.Error, Message = "检测已取消，可重新检测" }); }
        catch (Exception) { if (!_disposed) SetUpdateCheckState(false, new() { Status = UpdateCheckStatus.Error, Message = "暂时无法检测更新，请检查网络后重试" }); }
        finally
        {
            _checkingUpdate = false;
            if (!_disposed)
            {
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Content = "重新检测";
            }
        }
    }

    private void SetUpdateCheckState(bool checking, UpdateCheckResult? result = null)
    {
        _checkingUpdate = checking;
        _availableUpdate = !checking && result?.Status == UpdateCheckStatus.Available ? result : null;
        CheckUpdateButton.IsEnabled = !checking;
        CheckUpdateButton.Content = checking ? "检测中…" : "重新检测";
        DownloadUpdateButton.Visibility = _availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
        if (checking)
        {
            UpdateStatusText.Text = "正在连接 GitHub，检测正式版本…";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(80, 112, 129));
        }
        else if (result != null)
        {
            UpdateStatusText.Text = result.Message;
            UpdateStatusText.Foreground = new SolidColorBrush(result.Status == UpdateCheckStatus.Error ? Color.FromRgb(160, 104, 55) : Color.FromRgb(80, 112, 129));
            DownloadUpdateButton.Content = "下载新版";
            DownloadUpdateButton.ToolTip = "v" + result.LatestVersion + " · 在浏览器打开 GitHub 正式安装包，由你选择下载和安装";
        }
    }

    private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null || _demo) return;
        try { UpdateChecker.OpenDownload(_availableUpdate); }
        catch (Exception) { UpdateStatusText.Text = "无法打开浏览器，请重新检测后重试"; }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) { SettingsPopup.IsOpen = false; RequestExit(); }
    private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CaptionMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
    private void CaptionBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
        e.Handled = true;
    }
    private void ClearSearchButton_Click(object sender, RoutedEventArgs e) { TargetSearchBox.Clear(); TargetSearchBox.Focus(); }
    private void RankingItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RankingDisplayRow row })
        {
            SelectTab("targets");
            TargetSearchBox.Text = row.Name;
        }
    }

    private void TargetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _refreshingTargets) return;
        _selectedTargetKey = (TargetsGrid.SelectedItem as TargetDisplayRow)?.Key;
        UpdateTargetDetails();
    }

    private void UpdateTargetDetails()
    {
        var row = TargetsGrid.SelectedItem as TargetDisplayRow;
        TargetDetailBorder.Visibility = row != null && _selectedTab == "targets" ? Visibility.Visible : Visibility.Collapsed;
        if (row == null) { _selectedTargetKey = null; return; }
        DetailTitleText.Text = row.Target + "  ·  " + row.TotalDisplay;
        if (DetailBodyText.Text != row.Details) DetailBodyText.Text = row.Details;
    }

    private void CloseDetailButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedTargetKey = null;
        TargetsGrid.SelectedItem = null;
        TargetDetailBorder.Visibility = Visibility.Collapsed;
    }

    private void CopyDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsGrid.SelectedItem is not TargetDisplayRow row) return;
        try { Clipboard.SetText(row.Target + "\n" + row.Details + "\n上行：" + row.UploadDisplay + "  下行：" + row.DownloadDisplay + "  合计：" + row.TotalDisplay); }
        catch (Exception exception) { ShowError("无法复制", exception.Message); }
    }

    private void TargetSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(TargetSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(TargetSearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshSnapshot(false);
    }
    private void StartButton_Click(object sender, RoutedEventArgs e)
        => ToggleMonitoring();

    private void ToggleMonitoring()
    {
        if (_demo) return;
        try
        {
            if (_engine.IsRunning) _engine.Stop();
            else if (!_engine.IsAdmin)
            {
                if (Program.RelaunchElevated(_engine.IncludeChildren, _engine.IncludeLoopback)) RequestExit();
                return;
            }
            else
            {
                _engine.Start();
            }
            RefreshSnapshot(false);
        }
        catch (Exception exception) { ShowError("无法启动监控", exception.Message); }
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demo) return;
        if (Program.RelaunchElevated(_engine.IncludeChildren, _engine.IncludeLoopback)) RequestExit();
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _engine.IsRunning || _demo) return;
        _engine.IncludeChildren = ChildrenCheck.IsChecked == true;
        _engine.IncludeLoopback = LoopbackCheck.IsChecked == true;
        try { StartupManager.SaveCaptureOptions(_engine.IncludeChildren, _engine.IncludeLoopback); }
        catch (Exception exception) { ShowError("无法保存监控范围", exception.Message); }
        RefreshSnapshot(false);
    }

    private async Task RefreshStartupStatusAsync()
    {
        if (_startupChanging || _startupRefreshing || _disposed) return;
        _startupRefreshing = true;
        StartupCheck.IsEnabled = false;
        try
        {
            var status = await Task.Run(StartupManager.ReadStatus);
            if (!_disposed) ApplyStartupStatus(status);
        }
        catch (Exception exception)
        {
            if (!_disposed) StartupCheck.ToolTip = "无法读取开机自动监控状态：" + exception.Message;
        }
        finally
        {
            _startupRefreshing = false;
            if (!_disposed) StartupCheck.IsEnabled = !_startupChanging;
        }
    }

    private async void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _updatingStartupCheck || _startupChanging || _startupRefreshing || _demo || !_windowLoaded) return;
        bool enabled = StartupCheck.IsChecked == true;
        _startupChanging = true;
        StartupCheck.IsEnabled = false;
        StartupCheck.Content = "正在设置…";
        try
        {
            var status = await StartupManager.SetEnabledAsync(enabled);
            if (_disposed) return;
            ApplyStartupStatus(status);
            if (!string.IsNullOrWhiteSpace(status.Error)) ShowError("开机自动监控设置未完成", status.Error);
            else if (status.Enabled != enabled) ShowError("开机自动监控设置未完成", "系统中的开机启动状态没有变更，请重试。");
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            _updatingStartupCheck = true;
            StartupCheck.IsChecked = _startupEnabled;
            _updatingStartupCheck = false;
            try { ApplyStartupStatus(await Task.Run(StartupManager.ReadStatus)); }
            catch { /* Preserve the last confirmed state if the scheduler cannot be read. */ }
            if (_disposed) return;
            ShowError("开机自动监控设置未完成", exception.Message);
        }
        finally
        {
            _startupChanging = false;
            if (!_disposed)
            {
                StartupCheck.Content = "开机自动监控";
                StartupCheck.IsEnabled = true;
            }
        }
    }

    private void ApplyStartupStatus(StartupStatus status)
    {
        if (_disposed) return;
        _startupEnabled = status.Enabled;
        _updatingStartupCheck = true;
        StartupCheck.IsChecked = status.Enabled;
        _updatingStartupCheck = false;
        StartupCheck.ToolTip = "登录 Windows 后自动在后台开始记录。首次设置需确认 Windows UAC，仅注册计划任务，不保存密码。"
            + (status.Enabled ? "\n已启用，关闭窗口后仍会在系统托盘运行。" : "\n当前未启用。")
            + (!string.IsNullOrWhiteSpace(status.ExecutablePath) ? "\n启动程序：" + status.ExecutablePath : "")
            + (!string.IsNullOrWhiteSpace(status.Error) ? "\n" + status.Error : "");
        OptionsSummaryText.Text = (ChildrenCheck.IsChecked == true ? "派生工具已计入" : "仅核心进程") + (status.Enabled ? " · 自动启动已开启" : "");
    }

    private void HideToTray(bool notify = true)
    {
        if (_tray == null) return;
        ShowInTaskbar = false;
        Hide();
        if (notify) _tray.NotifyHidden(_engine.IsRunning);
    }

    public void RestoreFromTray()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RestoreFromTray)); return; }
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        RefreshSnapshot(false);
        Activate();
    }

    public void RequestExit()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RequestExit)); return; }
        _allowExit = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit || _demo || !string.IsNullOrWhiteSpace(PreviewOutputPath)) return;
        e.Cancel = true;
        HideToTray();
    }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _allowExit = true;
        Cleanup();
    }

    private void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        _updateCancellation.Cancel();
        _updateCancellation.Dispose();
        Application.Current.SessionEnding -= Application_SessionEnding;
        _timer.Stop();
        _tray?.Dispose();
        _engine.Dispose();
    }

    private void TargetsTab_Click(object sender, RoutedEventArgs e) => SelectTab("targets");
    private void ProcessesTab_Click(object sender, RoutedEventArgs e) => SelectTab("processes");
    private void SessionsTab_Click(object sender, RoutedEventArgs e) => SelectTab("sessions");

    private void SelectTab(string tab)
    {
        _selectedTab = tab;
        TargetsGrid.Visibility = tab == "targets" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesGrid.Visibility = tab == "processes" ? Visibility.Visible : Visibility.Collapsed;
        SessionsGrid.Visibility = tab == "sessions" ? Visibility.Visible : Visibility.Collapsed;
        SearchBorder.Visibility = tab == "targets" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in new[] { (TargetsTab, "targets"), (ProcessesTab, "processes"), (SessionsTab, "sessions") })
        {
            pair.Item1.Foreground = new SolidColorBrush(pair.Item2 == tab ? Color.FromRgb(27, 40, 53) : Color.FromRgb(132, 144, 155));
            pair.Item1.FontWeight = pair.Item2 == tab ? FontWeights.SemiBold : FontWeights.Normal;
        }
        TargetsTabText.TextDecorations = tab == "targets" ? TextDecorations.Underline : null;
        ProcessesTabText.TextDecorations = tab == "processes" ? TextDecorations.Underline : null;
        SessionsTabText.TextDecorations = tab == "sessions" ? TextDecorations.Underline : null;
        RefreshSnapshot(false);
    }

    private void UpdateEmptyState()
    {
        int count = _selectedTab == "targets" ? TargetsGrid.Items.Count : _selectedTab == "processes" ? ProcessesGrid.Items.Count : SessionsGrid.Items.Count;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool searching = !string.IsNullOrWhiteSpace(TargetSearchBox.Text);
        EmptyTitle.Text = _selectedTab == "sessions" ? "当前范围没有历史会话" : _selectedTab == "processes" ? "暂未发现" + (_applicationScope == DashboardApplicationScope.Total ? "监控" : ApplicationScopeLabel(_applicationScope)) + "进程" : _dashboard?.IsLoading == true ? "正在汇总历史记录…" : searching ? "没有匹配的流量去向" : _trafficScope == DashboardTrafficScope.Proxy ? "当前范围暂无已确认代理流量" : "当前范围还没有流量记录";
        EmptyHint.Text = _selectedTab == "sessions" ? "会话字节为所选应用的全部流量；每次停止监控后保存在本机" : _selectedTab == "processes" ? "当前进程显示所选应用的全部流量，与顶部历史累计分开统计" : searching ? "可搜索应用、域名、IP、地区、代理节点或路径类型" : _trafficScope == DashboardTrafficScope.Proxy ? "只包含记录时有代理证据的流量；未知未取得足够报告，不等于直连" : "开始监控后，用量会保存在本机，重启后继续累计";
        TableHintText.Text = ApplicationScopeLabel(_applicationScope) + " · " + (_selectedTab == "targets" ? RangeLabel(_range) + " · " + TrafficScopeLabel(_trafficScope) + " · 点击目标查看详情" : _selectedTab == "processes" ? "当前存活进程 · 全部流量 · 字节为本次监控值" : "全部流量 · 开始于所选时间范围的历史会话");
        TargetCountText.Text = (_selectedTab == "targets" && searching ? count + " / " + _targetTotalCount : count.ToString()) + (_selectedTab == "targets" ? " 个目标" : _selectedTab == "processes" ? " 个进程" : " 个会话");
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demo) return;
        var dialog = new SaveFileDialog { Title = "导出" + ApplicationScopeLabel(_applicationScope) + RangeLabel(_range) + TrafficScopeLabel(_trafficScope) + "去向", Filter = "CSV 文件 (*.csv)|*.csv", FileName = ApplicationScopeLabel(_applicationScope) + "_" + TrafficScopeLabel(_trafficScope) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _engine.ExportDashboardCsv(dialog.FileName, _range, _trafficScope, _applicationScope); CaptureHintText.Text = "已导出：" + dialog.FileName; }
        catch (Exception exception) { ShowError("导出失败", exception.Message); }
    }

    private void OpenDataButton_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_engine.DataDirectory); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _engine.DataDirectory + "\"") { UseShellExecute = true }); }
        catch (Exception exception) { ShowError("无法打开数据目录", exception.Message); }
    }

    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    private static string Bytes(double bytes) => TrafficChart.FormatBytes(bytes);
    private static string FormatAddress(string address, int port)
    {
        if (port <= 0) return address;
        if (address.StartsWith("[", StringComparison.Ordinal) && address.Contains("]:")) return address;
        if (address.Contains(':') && !address.StartsWith("[", StringComparison.Ordinal)) return "[" + address + "]:" + port;
        return address + ":" + port;
    }

    public void SavePreview(string path)
    {
        UpdateLayout();
        int width = (int)Math.Ceiling(ActualWidth);
        int height = (int)Math.Ceiling(ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static DashboardSnapshot CreateDemoDashboard(DashboardRange range, DashboardTrafficScope scope, DashboardApplicationScope applicationScope)
    {
        DateTime today = DateTime.Today;
        DateTime? first = range switch { DashboardRange.Today => today, DashboardRange.Last7Days => today.AddDays(-6), DashboardRange.Last30Days => today.AddDays(-29), _ => null };
        var history = Enumerable.Range(0, 30).Where(day => day % 8 != 3).Select(day => new DailyUsageRow
        {
            Date = today.AddDays(day - 29),
            UploadBytes = (long)((3.4 + Math.Abs(Math.Sin(day * .7)) * 6.1) * 1024 * 1024),
            DownloadBytes = (long)((21 + Math.Abs(Math.Sin(day * .43)) * 55 + (day is 8 or 17 or 25 ? 71 : 0)) * 1024 * 1024)
        }).ToList();
        var rows = history.Where(row => !first.HasValue || row.Date >= first).ToList();
        var destinations = new (TrackedApplication Application, string Host, string Address, int Port, string Process, string Route, string Region, string Node, int Percent, TrafficKind Kind)[]
        {
            (TrackedApplication.Codex, "chatgpt.com", "127.0.0.1", 7897, "Codex.exe", "回环代理", "美国 · US", "美国 01", 40, TrafficKind.Proxy),
            (TrackedApplication.Codex, "api.openai.com", "198.18.1.71", 443, "codex.exe", "虚拟地址", "美国 · US", "美国 01", 20, TrafficKind.Proxy),
            (TrackedApplication.Claude, "api.anthropic.com", "198.18.1.72", 443, "claude.exe", "虚拟地址", "美国 · US", "美国 01", 25, TrafficKind.Proxy),
            (TrackedApplication.Claude, "claude.ai", "198.18.1.73", 443, "Claude.exe", "虚拟地址", "日本 · JP", "日本 02", 5, TrafficKind.Proxy),
            (TrackedApplication.Codex, "api.github.com", "198.18.1.74", 443, "node.exe", "虚拟地址", "日本 · JP", "DIRECT", 5, TrafficKind.Direct),
            (TrackedApplication.Claude, "registry.npmjs.org", "198.18.1.75", 443, "node.exe", "虚拟地址", "美国 · US", "DIRECT", 3, TrafficKind.Direct),
            (TrackedApplication.Codex, "", "203.0.113.18", 443, "Codex.exe", "演示地址", "", "", 2, TrafficKind.Unknown)
        };
        bool ApplicationMatches(TrackedApplication application) => applicationScope == DashboardApplicationScope.Total || applicationScope == DashboardApplicationScope.Codex && application == TrackedApplication.Codex || applicationScope == DashboardApplicationScope.Claude && application == TrackedApplication.Claude;
        long Allocate(long value, int index) => index == destinations.Length - 1 ? value - destinations.Take(index).Sum(destination => value * destination.Percent / 100) : value * destinations[index].Percent / 100;
        long Portion(long value, Func<int, bool> include) => Enumerable.Range(0, destinations.Length).Where(include).Sum(index => Allocate(value, index));
        bool Selected(int index) => ApplicationMatches(destinations[index].Application) && (scope == DashboardTrafficScope.All || destinations[index].Kind == TrafficKind.Proxy);
        var allTargets = destinations.Select((row, index) => new TargetRow
        {
            Application = row.Application, Hostname = row.Host, Target = row.Address, Address = row.Address, Port = row.Port, Process = row.Process, Protocol = "TCP", Route = row.Route, Region = row.Region,
            RegionSource = "演示数据", ProxyNode = row.Node, ProxyChain = row.Kind == TrafficKind.Proxy ? "自动选择 → " + row.Node : row.Node,
            ProxyType = row.Kind == TrafficKind.Proxy ? "Shadowsocks" : row.Kind == TrafficKind.Direct ? "Direct" : "", TrafficKind = row.Kind,
            TrafficKindSource = "演示数据 · " + (row.Kind == TrafficKind.Proxy ? "代理报告节点和出口类型" : row.Kind == TrafficKind.Direct ? "出口类型 Direct" : "未取得代理报告"),
            DomainSource = row.Host.Length > 0 ? "代理报告 · 演示数据" : "", UploadBytes = rows.Sum(day => Allocate(day.UploadBytes, index)), DownloadBytes = rows.Sum(day => Allocate(day.DownloadBytes, index)), LastSeen = DateTime.Now
        }).ToList();
        var applicationTargets = allTargets.Where(row => ApplicationMatches(row.Application)).ToList();
        var targets = applicationTargets.Where(row => scope == DashboardTrafficScope.All || row.TrafficKind == TrafficKind.Proxy).ToList();
        long upload = applicationTargets.Sum(row => row.UploadBytes), download = applicationTargets.Sum(row => row.DownloadBytes);
        long proxyUp = applicationTargets.Where(row => row.TrafficKind == TrafficKind.Proxy).Sum(row => row.UploadBytes), proxyDown = applicationTargets.Where(row => row.TrafficKind == TrafficKind.Proxy).Sum(row => row.DownloadBytes);
        long directUp = applicationTargets.Where(row => row.TrafficKind == TrafficKind.Direct).Sum(row => row.UploadBytes), directDown = applicationTargets.Where(row => row.TrafficKind == TrafficKind.Direct).Sum(row => row.DownloadBytes);
        long ApplicationTotal(TrackedApplication application, bool up) => allTargets.Where(row => row.Application == application && (scope == DashboardTrafficScope.All || row.TrafficKind == TrafficKind.Proxy)).Sum(row => up ? row.UploadBytes : row.DownloadBytes);
        var selectedRows = rows.Select(day => new DailyUsageRow { Date = day.Date, UploadBytes = Portion(day.UploadBytes, Selected), DownloadBytes = Portion(day.DownloadBytes, Selected) }).ToList();
        var currentDay = history.Last();
        return new DashboardSnapshot
        {
            Range = range, TrafficScope = scope, ApplicationScope = applicationScope, FromUtc = first?.ToUniversalTime(), ToUtc = DateTime.UtcNow, FirstRecordedUtc = today.AddDays(-29).ToUniversalTime(),
            UploadBytes = targets.Sum(row => row.UploadBytes), DownloadBytes = targets.Sum(row => row.DownloadBytes),
            AllUploadBytes = upload, AllDownloadBytes = download, ProxyUploadBytes = proxyUp, ProxyDownloadBytes = proxyDown,
            DirectUploadBytes = directUp, DirectDownloadBytes = directDown, UnknownUploadBytes = upload - proxyUp - directUp, UnknownDownloadBytes = download - proxyDown - directDown,
            CodexUploadBytes = ApplicationTotal(TrackedApplication.Codex, true), CodexDownloadBytes = ApplicationTotal(TrackedApplication.Codex, false), ClaudeUploadBytes = ApplicationTotal(TrackedApplication.Claude, true), ClaudeDownloadBytes = ApplicationTotal(TrackedApplication.Claude, false),
            TodayUploadBytes = Portion(currentDay.UploadBytes, Selected), TodayDownloadBytes = Portion(currentDay.DownloadBytes, Selected),
            RecordedDays = rows.Count, SessionCount = rows.Count * 3 + 1, HasRecords = true, DailyUsage = selectedRows, Targets = targets
        };
    }

    private static MonitorSnapshot CreateDemoSnapshot(DashboardApplicationScope applicationScope)
    {
        var now = DateTime.Now;
        var allProcesses = new List<ProcessRow>
        {
            new() { Application = TrackedApplication.Codex, Name = "Codex.exe", Pid = 17420, Kind = "core", UploadBytes = 2_506_752, DownloadBytes = 17_612_800 },
            new() { Application = TrackedApplication.Codex, Name = "codex.exe", Pid = 18304, Kind = "core", UploadBytes = 163_840, DownloadBytes = 747_520 },
            new() { Application = TrackedApplication.Claude, Name = "Claude.exe", Pid = 18116, Kind = "core", UploadBytes = 1_112_064, DownloadBytes = 6_913_024 },
            new() { Application = TrackedApplication.Claude, Name = "node.exe", Pid = 18508, Kind = "tool", UploadBytes = 65_536, DownloadBytes = 327_680 }
        };
        var processes = allProcesses.Where(row => applicationScope == DashboardApplicationScope.Total || applicationScope == DashboardApplicationScope.Codex && row.Application == TrackedApplication.Codex || applicationScope == DashboardApplicationScope.Claude && row.Application == TrackedApplication.Claude).ToList();
        long codexUp = allProcesses.Where(row => row.Application == TrackedApplication.Codex).Sum(row => row.UploadBytes), codexDown = allProcesses.Where(row => row.Application == TrackedApplication.Codex).Sum(row => row.DownloadBytes);
        long claudeUp = allProcesses.Where(row => row.Application == TrackedApplication.Claude).Sum(row => row.UploadBytes), claudeDown = allProcesses.Where(row => row.Application == TrackedApplication.Claude).Sum(row => row.DownloadBytes);
        bool codex = applicationScope != DashboardApplicationScope.Claude, claude = applicationScope != DashboardApplicationScope.Codex;
        return new MonitorSnapshot
        {
            ApplicationScope = applicationScope, CapturedAt = now, StartedAt = now.AddMinutes(-18).AddSeconds(-42), HasCapturedData = true,
            UploadBytes = processes.Sum(row => row.UploadBytes), DownloadBytes = processes.Sum(row => row.DownloadBytes),
            CodexUploadBytes = codexUp, CodexDownloadBytes = codexDown, ClaudeUploadBytes = claudeUp, ClaudeDownloadBytes = claudeDown,
            UploadBytesPerSecond = (codex ? 6_820 : 0) + (claude ? 3_210 : 0), DownloadBytesPerSecond = (codex ? 42_800 : 0) + (claude ? 18_400 : 0),
            ProcessCount = processes.Count, ConnectionCount = processes.Count * 2, CaptureStatus = "正在记录", Processes = processes,
            Sessions = new List<SessionRow>
            {
                new() { StartedAt = now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss"), Duration = "00:35:12", UploadBytes = (codex ? 5_312_512 : 0) + (claude ? 2_621_440 : 0), DownloadBytes = (codex ? 33_554_432 : 0) + (claude ? 15_728_640 : 0) },
                new() { StartedAt = now.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"), Duration = "01:02:47", UploadBytes = (codex ? 12_521_472 : 0) + (claude ? 4_194_304 : 0), DownloadBytes = (codex ? 85_458_944 : 0) + (claude ? 20_971_520 : 0) }
            }
        };
    }
    private sealed class TargetDisplayRow
    {
        public required TargetRow Source { get; init; }
        private string? _key, _details;
        public string Key => _key ??= TargetIdentity(Source);
        public string Target => TargetName(Source);
        // DataGrid virtualizes rows. Format only realized cells and requested
        // details instead of eagerly building strings for the entire history.
        public string Details => _details ??= "所属应用：" + Source.Application + "\n直接连接：" + Address + "  ·  " + Source.Process + " / " + Protocol + "\n流量路径：" + TrafficKindLabel(Source.TrafficKind) + " · " + Source.TrafficKindSource + (!string.IsNullOrWhiteSpace(Source.DomainSource) ? "\n域名来源：" + Source.DomainSource : "") + (!string.IsNullOrEmpty(Source.ProxyDestination) ? "\n代理报告目标：" + Source.ProxyDestination : "") + "\n地区 / 网络：" + Region + "  ·  连接：" + DisplayRoute(Source) + "\n地区依据：" + ConnectionPresentation.RegionDetails(Source) + "\n代理节点：" + ProxyNode + "\n节点依据：" + ConnectionPresentation.NodeDetails(Source) + (!string.IsNullOrEmpty(Source.ProxyChain) ? "\n完整代理链：" + Source.ProxyChain : "");
        public string Address => FormatAddress(Source.Address, Source.Port);
        public string Process => Source.Application + " · " + (string.IsNullOrWhiteSpace(Source.Process) ? "—" : Source.Process) + " · " + Protocol;
        public string Protocol => Source.Protocol;
        public string Route => ShortTrafficKindLabel(Source.TrafficKind) + " · " + DisplayRoute(Source);
        public string Region => ConnectionPresentation.RegionLabel(Source);
        public string ProxyNode => ConnectionPresentation.NodeLabel(Source);
        public string ProxyContext => ConnectionPresentation.NodeContext(Source);
        public long UploadBytes => Source.UploadBytes;
        public long DownloadBytes => Source.DownloadBytes;
        public long TotalBytes => UploadBytes + DownloadBytes;
        public string UploadDisplay => Bytes(UploadBytes);
        public string DownloadDisplay => Bytes(DownloadBytes);
        public string TotalDisplay => Bytes(TotalBytes);
    }
    private sealed class ProcessDisplayRow
    {
        public string Name { get; init; } = "";
        public int Pid { get; init; }
        public string Kind { get; init; } = "";
        public long UploadBytes { get; init; }
        public long DownloadBytes { get; init; }
        public string UploadDisplay { get; init; } = "";
        public string DownloadDisplay { get; init; } = "";
    }
    private sealed class SessionDisplayRow
    {
        public string StartedAt { get; init; } = "";
        public string Duration { get; init; } = "";
        public long UploadBytes { get; init; }
        public long DownloadBytes { get; init; }
        public long TotalBytes => UploadBytes + DownloadBytes;
        public string UploadDisplay { get; init; } = "";
        public string DownloadDisplay { get; init; } = "";
        public string TotalDisplay { get; init; } = "";
    }
    private sealed class RankingDisplayRow
    {
        public string Rank { get; init; } = "";
        public string Name { get; init; } = "";
        public string Details { get; init; } = "";
        public string Display { get; init; } = "";
        public GridLength UsedWidth { get; init; }
        public GridLength RemainingWidth { get; init; }
    }
}
