using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTraffic;

/// <summary>Daily upload/download totals. Missing dates are shown as unrecorded, never as complete coverage.</summary>
public sealed class DailyUsageChart : FrameworkElement
{
    private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(103, 117, 131));
    private readonly Brush _up = new SolidColorBrush(Color.FromRgb(26, 167, 122));
    private readonly Brush _down = new SolidColorBrush(Color.FromRgb(66, 136, 220));
    private readonly Pen _grid = new(new SolidColorBrush(Color.FromRgb(236, 240, 244)), 1);
    private readonly Brush _selection = new SolidColorBrush(Color.FromArgb(18, 66, 136, 220));
    private readonly Pen _focus = new(new SolidColorBrush(Color.FromRgb(66, 136, 220)), 1) { DashStyle = DashStyles.Dot };
    private readonly Dictionary<DateTime, (long Up, long Down)> _rows = new();
    private readonly System.Windows.Controls.ToolTip _tooltip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private DateTime _first = DateTime.Today.AddDays(-29), _last = DateTime.Today;
    private Rect _plot;
    private int _hoverIndex = -1;
    private bool _loading;
    private bool _keyboardSelection;

    public DailyUsageChart()
    {
        MinHeight = 110;
        ClipToBounds = true;
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        _tooltip.PlacementTarget = this;
        System.Windows.Automation.AutomationProperties.SetName(this, "每日上下行流量堆叠柱状图，空白日期表示当天无记录");
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "按左右方向键查看前后一天，Home 查看首日，End 查看末日，Escape 关闭提示。");
        MouseLeave += (_, _) => { if (!_keyboardSelection) ClearSelection(); };
        MouseMove += OnMouseMove;
        GotKeyboardFocus += (_, _) =>
        {
            _keyboardSelection = true;
            if (_hoverIndex < 0) _hoverIndex = DayCount - 1;
            RefreshTooltip();
            InvalidateVisual();
        };
        LostKeyboardFocus += (_, _) => { if (_keyboardSelection) ClearSelection(); };
        Unloaded += (_, _) => _tooltip.IsOpen = false;
    }

    public void SetUsage(IEnumerable<DailyUsageRow> rows, DateTime first, DateTime last, bool loading)
    {
        DateTime? selectedDate = _hoverIndex < 0 ? null : _first.AddDays(_hoverIndex);
        bool rangeChanged = _first != first.Date || _last != last.Date;
        _rows.Clear();
        foreach (var row in rows)
        {
            var date = row.Date.Date;
            _rows.TryGetValue(date, out var previous);
            _rows[date] = (previous.Up + Math.Max(0, row.UploadBytes), previous.Down + Math.Max(0, row.DownloadBytes));
        }
        _first = first.Date;
        _last = last.Date < _first ? _first : last.Date;
        _loading = loading;
        if (rangeChanged)
        {
            if (_keyboardSelection && selectedDate.HasValue)
                _hoverIndex = Math.Clamp((int)(selectedDate.Value - _first).TotalDays, 0, DayCount - 1);
            else ClearSelection();
        }
        if (loading)
        {
            _tooltip.IsOpen = false;
        }
        else
        {
            // The selected day can keep receiving traffic while the pointer stays still.
            // Update the open tooltip on every snapshot, rather than waiting for MouseMove.
            if (_hoverIndex >= 0) RefreshTooltip();
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        if (width < 120 || height < 70) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        int days = DayCount;
        double largest = _rows.Where(row => row.Key >= _first && row.Key <= _last).Select(row => (double)row.Value.Up + row.Value.Down).DefaultIfEmpty().Max();
        AxisScale scale = CreateScale(largest);
        double maximum = scale.Maximum;
        double left = Math.Max(54, Text(scale.Label(scale.Intervals), 11, _muted).Width + 12);
        _plot = new Rect(left, 9, Math.Max(1, width - left - 10), height - 36);
        for (int i = 0; i <= scale.Intervals; i++)
        {
            double y = _plot.Top + _plot.Height * i / scale.Intervals;
            dc.DrawLine(_grid, new Point(_plot.Left, y), new Point(_plot.Right, y));
            var label = Text(scale.Label(scale.Intervals - i), 11, _muted);
            dc.DrawText(label, new Point(_plot.Left - label.Width - 10, y - label.Height / 2));
        }
        double step = _plot.Width / days;
        double barWidth = Math.Min(31, step * .62);
        if (_hoverIndex >= 0)
            dc.DrawRoundedRectangle(_selection, null, new Rect(_plot.Left + step * _hoverIndex, _plot.Top, step, _plot.Height), 3, 3);
        if (IsKeyboardFocusWithin)
            dc.DrawRoundedRectangle(null, _focus, new Rect(_plot.Left - 2, _plot.Top - 2, _plot.Width + 4, _plot.Height + 4), 3, 3);
        for (int i = 0; i < days; i++)
        {
            var date = _first.AddDays(i);
            if (!_rows.TryGetValue(date, out var value)) continue;
            double upHeight = value.Up / maximum * _plot.Height;
            double downHeight = value.Down / maximum * _plot.Height;
            double x = _plot.Left + step * i + (step - barWidth) / 2;
            if (downHeight > 0) dc.DrawRectangle(_down, null, new Rect(x, _plot.Bottom - downHeight, barWidth, downHeight));
            if (upHeight > 0) dc.DrawRectangle(_up, null, new Rect(x, _plot.Bottom - downHeight - upHeight, barWidth, upHeight));
        }
        int labelStep = Math.Max(1, (int)Math.Ceiling(days / 5d));
        for (int i = 0; i < days; i++)
        {
            if (i % labelStep != 0 && i != days - 1) continue;
            if (i != days - 1 && days - 1 - i < labelStep * .6) continue;
            string label = _first.AddDays(i).ToString("MM/dd", CultureInfo.InvariantCulture);
            var text = Text(label, 11, _muted);
            double x = Math.Clamp(_plot.Left + step * (i + .5) - text.Width / 2, _plot.Left, width - text.Width);
            dc.DrawText(text, new Point(x, _plot.Bottom + 9));
        }
        if (_loading || largest == 0)
        {
            string message = _loading ? "正在汇总历史用量…" : "当前范围暂无流量记录";
            var size = Text(message, 12, _muted);
            dc.DrawRectangle(Brushes.White, null, new Rect(_plot.Left, _plot.Top + _plot.Height * .35, _plot.Width, 26));
            dc.DrawText(size, new Point(_plot.Left + (_plot.Width - size.Width) / 2, _plot.Top + _plot.Height * .35 + 5));
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (!_plot.Contains(point) || _loading)
        {
            if (!_keyboardSelection) ClearSelection();
            return;
        }
        int days = DayCount;
        int index = Math.Clamp((int)((point.X - _plot.Left) / _plot.Width * days), 0, days - 1);
        if (index == _hoverIndex && _tooltip.IsOpen && !_keyboardSelection) return;
        _keyboardSelection = false;
        _hoverIndex = index;
        RefreshTooltip();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        if (_loading || !_plot.Contains(point)) return;
        Focus();
        _keyboardSelection = false;
        _hoverIndex = Math.Clamp((int)((point.X - _plot.Left) / _plot.Width * DayCount), 0, DayCount - 1);
        RefreshTooltip();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (_loading || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        int current = _hoverIndex < 0 ? DayCount - 1 : _hoverIndex;
        _hoverIndex = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(DayCount - 1, current + 1),
            Key.Home => 0,
            _ => DayCount - 1
        };
        _keyboardSelection = true;
        RefreshTooltip();
        InvalidateVisual();
        e.Handled = true;
    }

    private int DayCount => (int)(_last - _first).TotalDays + 1;

    private void RefreshTooltip()
    {
        if (_loading || _hoverIndex < 0 || _hoverIndex >= DayCount || _plot.Width <= 0)
        {
            _tooltip.IsOpen = false;
            return;
        }
        var date = _first.AddDays(_hoverIndex);
        string content = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        content += _rows.TryGetValue(date, out var row)
            ? "\n↑ 上行  " + TrafficChart.FormatBytes(row.Up) + "\n↓ 下行  " + TrafficChart.FormatBytes(row.Down) + "\n合计  " + TrafficChart.FormatBytes(row.Up + row.Down) + "\n仅包含已监控时段"
            : "\n当天无记录";
        _tooltip.Content = content;
        _tooltip.Placement = _keyboardSelection
            ? System.Windows.Controls.Primitives.PlacementMode.Top
            : System.Windows.Controls.Primitives.PlacementMode.Mouse;
        _tooltip.PlacementRectangle = _keyboardSelection
            ? new Rect(_plot.Left + _plot.Width / DayCount * (_hoverIndex + .5), _plot.Top, 1, 0)
            : Rect.Empty;
        System.Windows.Automation.AutomationProperties.SetItemStatus(this, content.Replace('\n', ' '));
        _tooltip.IsOpen = true;
    }

    private void ClearSelection()
    {
        _tooltip.IsOpen = false;
        _hoverIndex = -1;
        _keyboardSelection = false;
        System.Windows.Automation.AutomationProperties.SetItemStatus(this, string.Empty);
        InvalidateVisual();
    }

    private FormattedText Text(string value, double size, Brush brush) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private readonly record struct AxisScale(double UnitSize, string Unit, double Step, int Intervals)
    {
        public double Maximum => UnitSize * Step * Intervals;
        public string Label(int tick) => (tick * Step).ToString("#,0.##", CultureInfo.CurrentCulture) + " " + Unit;
    }

    private static AxisScale CreateScale(double value)
    {
        if (value <= 0) return new AxisScale(1024, "KB", .5, 2);
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double unitSize = 1;
        int unitIndex = 0;
        while (value >= unitSize * 1024 && unitIndex < units.Length - 1)
        {
            unitSize *= 1024;
            unitIndex++;
        }
        // Compare every tick in one unit. Choose two or three equal intervals with
        // a 1/2/2.5/5 step, rather than splitting a binary maximum into awkward thirds.
        double target = value / unitSize * 1.08;
        AxisScale best = default;
        foreach (int intervals in new[] { 3, 2 })
        {
            double roughStep = target / intervals;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
            double step = new[] { 1d, 2d, 2.5d, 5d, 10d }.First(factor => factor * magnitude >= roughStep) * magnitude;
            if (unitIndex == 0) step = Math.Max(1, step);
            var candidate = new AxisScale(unitSize, units[unitIndex], step, intervals);
            if (best.Maximum == 0 || candidate.Maximum < best.Maximum) best = candidate;
        }
        return best;
    }
}
