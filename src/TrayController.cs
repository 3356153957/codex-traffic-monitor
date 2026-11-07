using System;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexTraffic;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly TrayMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _monitorItem;
    private readonly Icon _appIcon;
    private readonly Dispatcher _dispatcher;
    private readonly Action _restore;
    private TrayNotificationWindow? _toast;
    private bool? _lastRunning;
    private bool _hasNotified;
    private bool _disposed;

    public TrayController(Dispatcher dispatcher, Action restore, Action toggleMonitoring, Action exit)
    {
        _dispatcher = dispatcher;
        _restore = restore;
        void OnUi(Action action)
        {
            if (_disposed || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
        }
        void Restore() { DismissHiddenNotification(); restore(); }
        _menu = new TrayMenuStrip();
        _menu.Items.Add(new TrayMenuHeader());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new TrayMenuItem("显示主窗口(&S)", TrayMenuGlyph.Window, () => OnUi(Restore)));
        _monitorItem = new TrayMenuItem("开始监控(&P)", TrayMenuGlyph.Play, () => OnUi(toggleMonitoring));
        _menu.Items.Add(_monitorItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new TrayMenuItem("退出程序(&X)", TrayMenuGlyph.Power, () => OnUi(exit)));
        _menu.ApplyDpi(_menu.DeviceDpi);
        using (var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/CodexTrafficMonitor;component/Assets/app.ico"))?.Stream)
            _appIcon = resource == null ? (Icon)SystemIcons.Application.Clone() : new Icon(resource, new Size(32, 32));
        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Codex · Claude 流量统计",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OnUi(Restore);
    }

    public void UpdateMonitoring(bool running)
    {
        if (_disposed || _lastRunning == running) return;
        _lastRunning = running;
        _monitorItem.Text = running ? "暂停监控(&P)" : "开始监控(&P)";
        _monitorItem.AccessibleName = running ? "暂停监控" : "开始监控";
        ((TrayMenuItem)_monitorItem).Glyph = running ? TrayMenuGlyph.Pause : TrayMenuGlyph.Play;
        _menu.Running = running;
        _menu.Invalidate();
        _toast?.UpdateMonitoring(running);
        _icon.Text = "Codex · Claude 流量统计 · " + (running ? "正在记录" : "监控已暂停");
    }

    public void NotifyHidden(bool running)
    {
        if (_disposed || _hasNotified) return;
        _hasNotified = true;
        if (_dispatcher.HasShutdownStarted) return;
        DismissHiddenNotification();
        var notification = new TrayNotificationWindow(running, () =>
        {
            if (!_disposed) _restore();
        });
        _toast = notification;
        notification.Closed += (_, _) => { if (ReferenceEquals(_toast, notification)) _toast = null; };
        notification.Present();
    }

    internal void DismissHiddenNotification() { _toast?.Dismiss(); _toast = null; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DismissHiddenNotification();
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
        _menu.Dispose();
    }
}
