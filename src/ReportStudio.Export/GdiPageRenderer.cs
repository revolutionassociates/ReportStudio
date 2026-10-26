using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Draws a RenderedPage with GDI+. Used by the viewer (screen), the image exporter and the printer,
/// so what you preview is exactly what you export.
/// </summary>
public static class GdiPageRenderer
{
    /// <summary>Renders into g, scaled so 1 report pixel = <paramref name="scale"/> device pixels.</summary>
    public static void Draw(Graphics g, RenderedPage page, float scale = 1f, bool paintBackground = true)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.ScaleTransform(scale, scale);

        if (paintBackground) g.Clear(Color.White);

        foreach (var item in page.Items)
            DrawItem(g, item);

        g.Restore(state);
    }

    public static void DrawItem(Graphics g, RenderedItem item)
    {
        var rect = new RectangleF((float)item.X, (float)item.Y, (float)item.Width, (float)item.Height);
        var back = ColorUtil.Parse(item.BackColor);
        var border = ColorUtil.Parse(item.BorderColor);

        switch (item.Kind)
        {
            case ItemKind.Line:
            {
                var c = ColorUtil.Parse(item.ForeColor) ?? Color.Black;
                using var pen = new Pen(c, (float)Math.Max(0.5, item.BorderWidth));
                g.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom);
                return;
            }
            case ItemKind.Rectangle:
            {
                if (back != null) { using var b = new SolidBrush(back.Value); g.FillRectangle(b, rect); }
                var bc = border ?? ColorUtil.Parse(item.ForeColor) ?? Color.Black;
                if (item.BorderWidth > 0)
                {
                    using var pen = new Pen(bc, (float)item.BorderWidth);
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                return;
            }
            case ItemKind.Image:
                DrawImage(g, item, rect);
                break;
            case ItemKind.Text:
                if (back != null) { using var b = new SolidBrush(back.Value); g.FillRectangle(b, rect); }
                DrawText(g, item, rect);
                break;
        }

        if (border != null && item.BorderWidth > 0)
        {
            using var pen = new Pen(border.Value, (float)item.BorderWidth);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private static void DrawImage(Graphics g, RenderedItem item, RectangleF rect)
    {
        if (item.Image == null || item.Image.Length == 0) return;
        try
        {
            using var ms = new MemoryStream(item.Image);
            using var img = Image.FromStream(ms);
            // Fit inside the box, keep aspect ratio, centre.
            float ratio = Math.Min(rect.Width / img.Width, rect.Height / img.Height);
            float w = img.Width * ratio, h = img.Height * ratio;
            g.DrawImage(img, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        }
        catch (ArgumentException) { /* unreadable image data: leave the box empty */ }
    }

    private static void DrawText(Graphics g, RenderedItem item, RectangleF rect)
    {
        if (string.IsNullOrEmpty(item.Text)) return;
        using var font = CreateFont(item.FontFamily, item.FontSize, item.Bold, item.Italic, item.Underline);
        using var brush = new SolidBrush(ColorUtil.Parse(item.ForeColor) ?? Color.Black);
        using var fmt = CreateFormat(item);
        var inner = RectangleF.Inflate(rect, -2, 0);
        g.DrawString(item.Text, font, brush, inner, fmt);
    }

    public static StringFormat CreateFormat(RenderedItem item)
    {
        var flags = item.WordWrap ? StringFormatFlags.NoClip : StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        return new StringFormat(flags)
        {
            Alignment = item.HAlign switch { HAlign.Center => StringAlignment.Center, HAlign.Right => StringAlignment.Far, _ => StringAlignment.Near },
            LineAlignment = item.VAlign switch { VAlign.Middle => StringAlignment.Center, VAlign.Bottom => StringAlignment.Far, _ => StringAlignment.Near },
            Trimming = StringTrimming.EllipsisCharacter
        };
    }

    /// <summary>Font sizes are in points; the Graphics is in 96-dpi "report pixels", so convert pt -> px.</summary>
    public static Font CreateFont(string family, double sizePt, bool bold, bool italic, bool underline)
    {
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        if (underline) style |= FontStyle.Underline;
        float px = (float)(sizePt * 96.0 / 72.0);
        // Catalog fonts (Ubuntu) work even when they are not installed on the machine.
        var shipped = FontCatalog.FindFamily(family);
        if (shipped != null && shipped.IsStyleAvailable(style & ~FontStyle.Underline))
            return new Font(shipped, px, style, GraphicsUnit.Pixel);
        try { return new Font(family, px, style, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
    }
}

public static class ColorUtil
{
    /// <summary>Parses "#RRGGBB", "#AARRGGBB" or a colour name. Empty or invalid text returns null (transparent).</summary>
    public static Color? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        try
        {
            if (text.StartsWith('#'))
            {
                var hex = text.Substring(1);
                if (hex.Length == 6) return Color.FromArgb(Convert.ToInt32(hex, 16) | unchecked((int)0xFF000000));
                if (hex.Length == 8) return Color.FromArgb(unchecked((int)Convert.ToUInt32(hex, 16)));
                return null;
            }
            var named = Color.FromName(text);
            return named.IsKnownColor ? named : null;
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }
}
