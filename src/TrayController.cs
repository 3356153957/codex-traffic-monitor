using System;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexTraffic;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _monitorItem;
    private readonly Icon _appIcon;
    private bool _hasNotified;
    private bool _disposed;

    public TrayController(Dispatcher dispatcher, Action restore, Action toggleMonitoring, Action exit)
    {
        void OnUi(Action action)
        {
            if (_disposed || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(action);
        }
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("显示窗口", null, (_, _) => OnUi(restore));
        _monitorItem = new Forms.ToolStripMenuItem("开始监控", null, (_, _) => OnUi(toggleMonitoring));
        _menu.Items.Add(_monitorItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => OnUi(exit));
        using (var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/CodexTrafficMonitor;component/Assets/app.ico"))?.Stream)
            _appIcon = resource == null ? (Icon)SystemIcons.Application.Clone() : new Icon(resource, new Size(32, 32));
        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Codex 流量统计",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OnUi(restore);
    }

    public void UpdateMonitoring(bool running)
    {
        if (_disposed) return;
        _monitorItem.Text = running ? "暂停监控" : "开始监控";
        _icon.Text = "Codex 流量统计 · " + (running ? "正在记录" : "监控已暂停");
    }

    public void NotifyHidden(bool running)
    {
        if (_disposed || _hasNotified) return;
        _hasNotified = true;
        _icon.ShowBalloonTip(3500, "Codex 流量统计已收起到托盘", running
            ? "流量监控继续运行。双击托盘图标查看，右键选择“退出”结束程序。"
            : "当前未开始监控。双击托盘图标查看，右键可开始监控或退出程序。", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
        _menu.Dispose();
    }
}
