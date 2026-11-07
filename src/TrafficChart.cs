using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexTraffic;

/// <summary>A small, dependency-free, one minute network rate chart.</summary>
public sealed class TrafficChart : FrameworkElement
{
    private readonly List<(double Up, double Down)> _samples = new();
    private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(153, 164, 175));
    private readonly Pen _grid = new(new SolidColorBrush(Color.FromRgb(236, 240, 244)), 1);
    private readonly Brush _up = new SolidColorBrush(Color.FromRgb(26, 167, 122));
    private readonly Brush _down = new SolidColorBrush(Color.FromRgb(66, 136, 220));
    public bool IsPreview { get; set; }

    public TrafficChart() { MinHeight = 120; ClipToBounds = true; }

    public void AddSample(double upload, double download)
    {
        _samples.Add((Math.Max(0, upload), Math.Max(0, download)));
        if (_samples.Count > 61) _samples.RemoveAt(0);
        InvalidateVisual();
    }

    public void Clear() { _samples.Clear(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        if (width < 120 || height < 70) return;
        var plot = new Rect(62, 8, width - 70, height - 35);
        double largest = 0;
        foreach (var sample in _samples) largest = Math.Max(largest, Math.Max(sample.Up, sample.Down));
        double maximum = NiceMaximum(largest);

        for (int i = 0; i <= 3; i++)
        {
            double y = plot.Top + plot.Height * i / 3;
            dc.DrawLine(_grid, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawLabel(dc, FormatBytes(maximum * (3 - i) / 3) + "/s", new Point(0, y - 6), 10, _muted);
        }
        string[] labels = { "60 秒前", "45 秒", "30 秒", "15 秒", "现在" };
        for (int i = 0; i < labels.Length; i++)
        {
            double x = plot.Left + plot.Width * i / 4;
            DrawLabel(dc, labels[i], new Point(x - (i == 4 ? 22 : i == 0 ? 0 : 12), plot.Bottom + 10), 10, _muted);
        }
        if (_samples.Count > 1)
        {
            DrawSeries(dc, plot, maximum, false, _down, Color.FromArgb(21, 66, 136, 220));
            DrawSeries(dc, plot, maximum, true, _up, Color.FromArgb(15, 26, 167, 122));
        }
        else
        {
            var message = "开始监控后显示实时速率";
            var type = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, type, 12, _muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(plot.Left + (plot.Width - text.Width) / 2, plot.Top + plot.Height / 2 - text.Height / 2));
        }
    }

    private void DrawSeries(DrawingContext dc, Rect plot, double maximum, bool upload, Brush color, Color fill)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        double firstX = plot.Right - plot.Width * (_samples.Count - 1) / 60;
        using (var l = line.Open())
        using (var a = area.Open())
        {
            a.BeginFigure(new Point(firstX, plot.Bottom), true, true);
            for (int i = 0; i < _samples.Count; i++)
            {
                double x = firstX + plot.Width * i / 60;
                double value = upload ? _samples[i].Up : _samples[i].Down;
                var point = new Point(x, plot.Bottom - Math.Min(1, value / maximum) * plot.Height);
                if (i == 0) l.BeginFigure(point, false, false); else l.LineTo(point, true, false);
                a.LineTo(point, true, false);
            }
            a.LineTo(new Point(plot.Right, plot.Bottom), true, false);
        }
        line.Freeze(); area.Freeze();
        dc.DrawGeometry(new SolidColorBrush(fill), null, area);
        dc.DrawGeometry(null, new Pen(color, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, line);
    }

    private void DrawLabel(DrawingContext dc, string value, Point point, double size, Brush brush)
    {
        var type = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        dc.DrawText(new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, type, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    }

    private static double NiceMaximum(double value)
    {
        if (value < 1024) return 1024;
        double magnitude = Math.Pow(1024, Math.Floor(Math.Log(value, 1024)));
        double relative = value * 1.18 / magnitude;
        foreach (double step in new[] { 1d, 2d, 4d, 8d, 16d, 32d, 64d, 128d, 256d, 512d, 1024d })
            if (relative <= step) return magnitude * step;
        return value * 1.18;
    }

    internal static string FormatBytes(double value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
