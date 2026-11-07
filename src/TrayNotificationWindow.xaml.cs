using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexTraffic;

internal sealed partial class TrayNotificationWindow : Window
{
    private readonly Action _restore;
    private readonly DispatcherTimer _timer;
    private System.Drawing.Rectangle _workingArea;
    private HwndSource? _source;
    private bool _closed;

    internal TrayNotificationWindow(bool running, Action restore)
    {
        InitializeComponent();
        _restore = restore;
        // No permanent timer or notification polling: this window exists only for its five-second hint.
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += Timer_Tick;
        SourceInitialized += Window_SourceInitialized;
        Loaded += (_, _) => PlaceInWorkingArea();
        Closed += Window_Closed;
        UpdateMonitoring(running);
    }

    internal void UpdateMonitoring(bool running)
    {
        DescriptionText.Text = running
            ? "流量会在后台继续累计。\n双击托盘图标即可重新打开窗口。"
            : "当前监控已暂停，可在托盘菜单开始监控。\n双击托盘图标即可重新打开窗口。";
        var color = (Color)ColorConverter.ConvertFromString(running ? "#147D5F" : "#667B8B");
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        StatusDot.Fill = brush;
        StatusText.Foreground = brush;
        StatusText.Text = running ? "正在后台记录" : "监控已暂停";
    }

    internal void Present()
    {
        if (_closed) return;
        _workingArea = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        // Start outside the desktop. Native placement below uses physical pixels even on mixed-DPI monitors.
        Left = -32000;
        Top = -32000;
        Show();
        PlaceInWorkingArea();
        _timer.Start();
    }

    internal void Dismiss()
    {
        if (!_closed) Close();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // WS_EX_NOACTIVATE keeps focus in the user's current app, including clicks on the close action.
        // WS_EX_TOOLWINDOW also excludes this short-lived hint from Alt+Tab.
        const int exStyle = -20;
        SetWindowLongPtr(hwnd, exStyle, new IntPtr(GetWindowLongPtr(hwnd, exStyle).ToInt64() | 0x08000000L | 0x00000080L));
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowMessage);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // MA_NOACTIVATE: mouse actions remain usable while the toast itself never takes foreground focus.
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        if (message is 0x02E0 or 0x007E) Dispatcher.BeginInvoke(new Action(PlaceInWorkingArea), DispatcherPriority.Loaded);
        return IntPtr.Zero;
    }

    private void PlaceInWorkingArea()
    {
        if (_closed || !IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Re-check the original monitor's work area if the taskbar/display changes while the hint is open.
        var point = new System.Drawing.Point(_workingArea.Right - 1, _workingArea.Bottom - 1);
        _workingArea = Forms.Screen.FromPoint(point).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * scale.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * scale.DpiScaleY);
        int inset = (int)Math.Round(5 * scale.DpiScaleX);
        int left = Math.Max(_workingArea.Left, _workingArea.Right - width - inset);
        int top = Math.Max(_workingArea.Top, _workingArea.Bottom - height - inset);
        SetWindowPos(hwnd, new IntPtr(-1), left, top, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE
    }

    private void Restore_Click(object sender, RoutedEventArgs e) { Dismiss(); _restore(); }
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void Timer_Tick(object? sender, EventArgs e) => Dismiss();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _source?.RemoveHook(WindowMessage);
        _source = null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
