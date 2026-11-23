using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace StreamHelper.Ui;

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color Back = Color.FromArgb(0x26, 0x2A, 0x33);
    public static readonly Color Line = Color.FromArgb(0x3A, 0x3F, 0x4B);
    public static readonly Color Hover = Color.FromArgb(0x51, 0x55, 0x5C);
    public static readonly Color Divider = Color.FromArgb(0x33, 0x37, 0x42);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE8, 0xEE);
    public static readonly Color Muted = Color.FromArgb(0x8B, 0x91, 0xA0);

    public const int BorderColorRef = 0x4B3F3A;

    private const int HoverInset = 4;
    private const int HoverRadius = 4;
    private const int SeparatorInset = 8;
    private const int DividerInset = 10;

    public TrayMenuRenderer() => RoundedEdges = false;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Back);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
        using var pen = new Pen(Divider);
        var items = e.ToolStrip.Items.Cast<ToolStripItem>().Where(item => item.Available).ToList();
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i] is ToolStripSeparator || items[i - 1] is ToolStripSeparator) continue;
            var y = items[i].Bounds.Top;
            e.Graphics.DrawLine(pen, DividerInset, y, e.ToolStrip.Width - DividerInset, y);
        }
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Line);
        var bounds = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var visibleWidth = Math.Min(e.Item.Width, e.ToolStrip.Width - e.Item.Bounds.X);
        var bounds = new Rectangle(HoverInset, 1, visibleWidth - HoverInset * 2, e.Item.Height - 2);
        using var path = Rounded(bounds, HoverRadius);
        using var brush = new SolidBrush(Hover);
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
        e.Graphics.SmoothingMode = smoothing;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Muted;
        if (e.Item is ToolStripMenuItem && e.TextFont != null)
        {
            var height = TextRenderer.MeasureText(e.Graphics, e.Text, e.TextFont, new Size(int.MaxValue, int.MaxValue), e.TextFormat).Height;
            var text = e.TextRectangle;
            e.TextRectangle = new Rectangle(text.X, (e.Item.Height - height + 1) / 2, text.Width, height);
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Line);
        e.Graphics.DrawLine(pen, SeparatorInset, y, e.Item.Width - SeparatorInset, y);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
