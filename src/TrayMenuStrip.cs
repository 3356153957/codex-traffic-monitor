using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace CodexTraffic;

internal enum TrayMenuGlyph { Window, Pause, Play, Power }

internal sealed class TrayMenuItem : Forms.ToolStripMenuItem
{
    internal TrayMenuGlyph Glyph { get; set; }
    internal TrayMenuItem(string text, TrayMenuGlyph glyph, Action action) : base(text, null, (_, _) => action())
    {
        Glyph = glyph;
        AutoSize = false;
        AccessibleName = text.Replace("(&S)", "").Replace("(&P)", "").Replace("(&X)", "");
    }
}

internal sealed class TrayMenuHeader : Forms.ToolStripMenuItem
{
    internal TrayMenuHeader() : base("Codex · Claude 流量统计")
    {
        Enabled = false;
        AutoSize = false;
        AccessibleRole = Forms.AccessibleRole.StaticText;
    }
}

// Keep the shell's native menu activation, keyboard navigation and multi-monitor placement.
// Only drawing and spacing are customized; no extra polling or hidden owner window is needed.
internal sealed class TrayMenuStrip : Forms.ContextMenuStrip
{
    private Font? _menuFont;
    private Font? _titleFont;
    private Font? _captionFont;
    internal float DpiScale { get; private set; } = 1;
    internal bool Running { get; set; }
    internal Font TitleFont => _titleFont ?? Font;
    internal Font CaptionFont => _captionFont ?? Font;

    internal TrayMenuStrip()
    {
        AutoSize = false;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(38, 51, 64);
        Renderer = new TrayMenuRenderer();
        AccessibleName = "Codex 与 Claude 流量统计托盘菜单";
        ApplyDpi(DeviceDpi);
    }

    internal int Px(float value) => (int)Math.Round(value * DpiScale);

    internal void ApplyDpi(int dpi)
    {
        DpiScale = Math.Clamp(dpi, 96, 768) / 96f;
        var oldMenu = _menuFont;
        var oldTitle = _titleFont;
        var oldCaption = _captionFont;
        _menuFont = new Font("Microsoft YaHei UI", 13 * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        _titleFont = new Font("Segoe UI", 13 * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        _captionFont = new Font("Microsoft YaHei UI", 10.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = _menuFont;
        Padding = new Forms.Padding(Px(7));
        int height = Padding.Vertical;
        foreach (Forms.ToolStripItem item in Items)
        {
            item.AutoSize = false;
            item.Margin = Forms.Padding.Empty;
            item.Padding = Forms.Padding.Empty;
            item.Size = new Size(Px(242), Px(item is TrayMenuHeader ? 58 : item is Forms.ToolStripSeparator ? 9 : 38));
            height += item.Height;
        }
        Size = new Size(Px(256), height);
        oldMenu?.Dispose(); oldTitle?.Dispose(); oldCaption?.Dispose();
        ApplyRoundedRegion();
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        ApplyDpi(DeviceDpi);
        foreach (Forms.ToolStripItem item in Items)
            if (item is TrayMenuHeader) item.AccessibleName = "Codex 与 Claude 流量统计，" + (Running ? "正在后台记录" : "监控已暂停");
        base.OnOpening(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyDpi(DeviceDpi); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ApplyRoundedRegion(); }

    private void ApplyRoundedRegion()
    {
        if (Width < 2 || Height < 2) return;
        using var path = TrayMenuRenderer.Rounded(new RectangleF(0, 0, Width, Height), Px(10));
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _menuFont?.Dispose(); _titleFont?.Dispose(); _captionFont?.Dispose(); }
    }
}

internal sealed class TrayMenuRenderer : Forms.ToolStripRenderer
{
    private static readonly Color Ink = Color.FromArgb(38, 51, 64);
    private static readonly Color Muted = Color.FromArgb(102, 123, 139);
    private static readonly Color Green = Color.FromArgb(20, 125, 95);

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Color.White);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not TrayMenuStrip menu) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new RectangleF(.5f, .5f, menu.Width - 1, menu.Height - 1), menu.Px(10));
        using var pen = new Pen(Color.FromArgb(217, 225, 232));
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not TrayMenuItem item || !item.Selected) return;
        if (e.ToolStrip is not TrayMenuStrip menu) return;
        var rectangle = new RectangleF(0, 0, item.Width - 1, item.Height - 1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(rectangle, menu.Px(6));
        using var fill = new SolidBrush(item.Glyph == TrayMenuGlyph.Power ? Color.FromArgb(252, 239, 239) : Color.FromArgb(235, 242, 248));
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        if (e.ToolStrip is not TrayMenuStrip menu) return;
        var g = e.Graphics;
        if (e.Item is TrayMenuHeader)
        {
            var badge = new RectangleF(menu.Px(9), menu.Px(11), menu.Px(32), menu.Px(32));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(badge, menu.Px(8)))
            using (var fill = new SolidBrush(Color.FromArgb(25, 44, 59))) g.FillPath(fill, path);
            using (var pen = new Pen(Color.FromArgb(197, 241, 223), 1.6f * menu.DpiScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                for (int i = 0; i < 4; i++)
                {
                    float x = badge.Left + menu.Px(9 + i * 5);
                    float top = badge.Top + menu.Px(i % 2 == 0 ? 12 : 8);
                    float bottom = badge.Top + menu.Px(i == 1 ? 25 : 22);
                    g.DrawLine(pen, x, top, x, bottom);
                }
            }
            Text(g, "Codex · Claude", menu.TitleFont, Ink, new Rectangle(menu.Px(51), menu.Px(9), menu.Px(180), menu.Px(21)));
            string status = menu.Running ? "流量统计 · 正在记录" : "流量统计 · 监控已暂停";
            Text(g, status, menu.CaptionFont, menu.Running ? Green : Muted, new Rectangle(menu.Px(51), menu.Px(30), menu.Px(180), menu.Px(18)));
            return;
        }
        if (e.Item is not TrayMenuItem action) return;
        Color ink = action.Glyph == TrayMenuGlyph.Power ? Color.FromArgb(143, 83, 88) : Ink;
        DrawGlyph(g, action.Glyph, new RectangleF(menu.Px(11), (action.Height - menu.Px(18)) / 2f, menu.Px(18), menu.Px(18)), ink);
        var flags = Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding;
        if ((e.TextFormat & Forms.TextFormatFlags.HidePrefix) != 0) flags |= Forms.TextFormatFlags.HidePrefix;
        Forms.TextRenderer.DrawText(g, e.Text, menu.Font, new Rectangle(menu.Px(42), 0, action.Width - menu.Px(50), action.Height), ink, flags);
    }

    private static void Text(Graphics graphics, string text, Font font, Color color, Rectangle bounds)
        => Forms.TextRenderer.DrawText(graphics, text, font, bounds, color, Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine);

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        if (e.ToolStrip is not TrayMenuStrip menu) return;
        using var pen = new Pen(Color.FromArgb(233, 237, 241));
        e.Graphics.DrawLine(pen, menu.Px(10), e.Item.Height / 2, e.Item.Width - menu.Px(10), e.Item.Height / 2);
    }

    private static void DrawGlyph(Graphics g, TrayMenuGlyph glyph, RectangleF bounds, Color color)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.Left, bounds.Top);
        g.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
        using var pen = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (glyph)
        {
            case TrayMenuGlyph.Window:
                using (var path = Rounded(new RectangleF(3, 4, 18, 16), 2)) g.DrawPath(pen, path);
                g.DrawLine(pen, 3, 9, 21, 9); g.DrawLine(pen, 6, 6.5f, 6.1f, 6.5f); break;
            case TrayMenuGlyph.Pause:
                g.DrawLine(pen, 8, 5, 8, 19); g.DrawLine(pen, 16, 5, 16, 19); break;
            case TrayMenuGlyph.Play:
                g.DrawPolygon(pen, new[] { new PointF(8, 4), new PointF(20, 12), new PointF(8, 20) }); break;
            case TrayMenuGlyph.Power:
                g.DrawArc(pen, new RectangleF(4, 4, 16, 16), -52, 284); g.DrawLine(pen, 12, 2, 12, 11); break;
        }
        g.Restore(state);
    }
}
