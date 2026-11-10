using System.Drawing;
using ReportStudio.Core.Barcodes;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Text;

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

        DrawBackground(g, page);        // first of all
        DrawWatermark(g, page);         // then the watermark, behind the items

        foreach (var item in page.Items)
            DrawItem(g, item);

        g.Restore(state);
    }

    /// <summary>The colour or gradient behind the whole page. A colour that cannot be read leaves the page plain.</summary>
    private static void DrawBackground(Graphics g, RenderedPage page)
    {
        var bg = page.Background;
        if (bg == null || page.Width <= 0 || page.Height <= 0 || ColorUtil.Parse(bg.Color) is not { } color) return;

        var pageRect = new RectangleF(0, 0, (float)page.Width, (float)page.Height);
        using var brush = FillBrush(pageRect, color, bg.GradientColor, bg.GradientDirection);
        // a pixel beyond the page on every side, so a scale that is not a whole number leaves no white hairline at the edge
        g.FillRectangle(brush, -1, -1, pageRect.Width + 2, pageRect.Height + 2);
    }

    /// <summary>Faint text turned about the page centre. A text too long for the page is made smaller until it fits.</summary>
    private static void DrawWatermark(Graphics g, RenderedPage page)
    {
        var wm = page.Watermark;
        if (wm == null || page.Width <= 0 || page.Height <= 0) return;
        int alpha = WatermarkLayout.Alpha(wm.Opacity);
        if (alpha == 0) return;

        DrawWatermarkPicture(g, page, wm, alpha);       // the picture first, so any text lies over it
        if (string.IsNullOrWhiteSpace(wm.Text)) return;

        var text = wm.Text.Replace('\r', ' ').Replace('\n', ' ');
        var color = ColorUtil.Parse(wm.Color) ?? Color.Gray;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
        };

        double size = wm.FontSize;
        using (var probe = CreateFont(ReportItem.DefaultFontFamily, size, wm.Bold, false, false))
            size = WatermarkLayout.FitFontSize(size, g.MeasureString(text, probe, PointF.Empty, format).Width, page.Width, page.Height, wm.Angle);

        using var font = CreateFont(ReportItem.DefaultFontFamily, size, wm.Bold, false, false);
        using var brush = new SolidBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        var state = g.Save();
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;       // ClearType cannot blend with a see-through colour
        g.TranslateTransform((float)page.Width / 2, (float)page.Height / 2);
        g.RotateTransform((float)-wm.Angle);                            // GDI+ turns clockwise; the setting turns counter-clockwise
        g.DrawString(text, font, brush, PointF.Empty, format);
        g.Restore(state);
    }

    /// <summary>The watermark picture, centred, turned and faded like the text. An unreadable picture leaves the page as it is.</summary>
    private static void DrawWatermarkPicture(Graphics g, RenderedPage page, RenderedWatermark wm, int alpha)
    {
        if (wm.Image == null || wm.Image.Length == 0) return;
        try
        {
            var picture = SvgRaster.Resolve(wm.Image, page.Width * wm.ImageSize / 100, page.Height, 1);     // an SVG drawing becomes a PNG
            if (picture.Length == 0) return;
            using var ms = new MemoryStream(picture);
            using var img = Image.FromStream(ms);
            var (w, h) = WatermarkLayout.PictureSize(img.Width, img.Height, page.Width, page.Height, wm.Angle, wm.ImageSize);
            if (w <= 0 || h <= 0) return;

            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha / 255f });   // see-through, keeping the picture's own transparency
            var state = g.Save();
            g.TranslateTransform((float)page.Width / 2, (float)page.Height / 2);
            g.RotateTransform((float)-wm.Angle);                            // GDI+ turns clockwise; the setting turns counter-clockwise
            g.DrawImage(img, new RectangleF((float)(-w / 2), (float)(-h / 2), (float)w, (float)h),
                new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, attributes);
            g.Restore(state);
        }
        catch (ArgumentException) { /* unreadable picture: leave it out */ }
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
            case ItemKind.Ellipse:
                {
                    var bc = border ?? ColorUtil.Parse(item.ForeColor) ?? Color.Black;
                    bool ellipse = item.Kind == ItemKind.Ellipse;
                    double radius = ellipse ? 0 : ShapeGeometry.CornerRadius(item.CornerRadius, rect.Width, rect.Height);
                    if (ellipse || radius > 0)
                    {
                        if (rect.Width <= 0 || rect.Height <= 0) return;
                        using var path = ellipse ? EllipsePath(rect) : RoundedPath(rect, (float)radius);
                        if (back != null) { using var b = FillBrush(item, rect, back.Value); g.FillPath(b, path); }
                        if (item.BorderWidth > 0)
                        {
                            using var outline = new Pen(bc, (float)item.BorderWidth);
                            g.DrawPath(outline, path);
                        }
                        return;
                    }

                    if (back != null) { using var b = FillBrush(item, rect, back.Value); g.FillRectangle(b, rect); }
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
            case ItemKind.Barcode:
                if (back != null) { using var b = new SolidBrush(back.Value); g.FillRectangle(b, rect); }
                DrawBarcode(g, item, rect);
                break;
            case ItemKind.QrCode:
                if (back != null) { using var b = new SolidBrush(back.Value); g.FillRectangle(b, rect); }
                DrawQr(g, item, rect);
                break;
            case ItemKind.Barcode2D:
                if (back != null) { using var b = new SolidBrush(back.Value); g.FillRectangle(b, rect); }
                DrawCells(g, item, rect, Symbol2DLayout.Rectangles(item.Barcode2DType, item.Text, rect.Width, rect.Height));
                break;
            case ItemKind.Text:
                if (back != null) { using var b = FillBrush(item, rect, back.Value); g.FillRectangle(b, rect); }
                if (TextTurn.IsTurned(item.TextRotation) && !string.IsNullOrEmpty(item.Text))
                {
                    // Turn the text about the middle of the box; the fill and border stay where they are.
                    var (lw, lh) = TextTurn.LayoutSize(item.TextRotation, rect.Width, rect.Height);
                    var state = g.Save();
                    g.TranslateTransform(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                    g.RotateTransform((float)-TextTurn.Normalize(item.TextRotation));
                    DrawText(g, item, new RectangleF((float)(-lw / 2), (float)(-lh / 2), (float)lw, (float)lh));
                    g.Restore(state);
                }
                else DrawText(g, item, rect);
                break;
        }

        if (border != null && item.BorderWidth > 0)
        {
            using var pen = new Pen(border.Value, (float)item.BorderWidth);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    /// <summary>The brush for an item's fill: a gradient from the back colour to the gradient colour, or the plain back colour.</summary>
    private static Brush FillBrush(RenderedItem item, RectangleF rect, Color back) =>
        FillBrush(rect, back, ShapeGeometry.UsesGradient(item) ? item.GradientColor : "", item.GradientDirection);

    /// <summary>The same, from the pieces: a gradient when <paramref name="gradientColor"/> is a readable colour, otherwise the plain colour.</summary>
    private static Brush FillBrush(RectangleF rect, Color back, string gradientColor, GradientDirection direction)
    {
        if (!string.IsNullOrWhiteSpace(gradientColor) && ColorUtil.Parse(gradientColor) is { } second && rect.Width > 0 && rect.Height > 0)
        {
            var mode = direction == GradientDirection.LeftToRight ? LinearGradientMode.Horizontal : LinearGradientMode.Vertical;
            // TileFlipXY keeps the first and last pixel rows from picking up the opposite colour at the edge.
            return new LinearGradientBrush(rect, back, second, mode) { WrapMode = WrapMode.TileFlipXY };
        }
        return new SolidBrush(back);
    }

    private static GraphicsPath EllipsePath(RectangleF rect)
    {
        var path = new GraphicsPath();
        path.AddEllipse(rect);
        return path;
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawQr(Graphics g, RenderedItem item, RectangleF rect)
    {
        DrawCells(g, item, rect, QrLayout.Rectangles(item.Text, item.QrLevel, rect.Width, rect.Height));
    }

    private static void DrawCells(Graphics g, RenderedItem item, RectangleF rect, IReadOnlyList<(double X, double Y, double W, double H)> cells)
    {
        if (cells.Count == 0) return;

        // One filled path (not many rectangles) so anti-aliasing leaves no hairline gaps between modules.
        using var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Winding);
        foreach (var (x, y, w, h) in cells)
            path.AddRectangle(new RectangleF(rect.X + (float)x, rect.Y + (float)y, (float)w, (float)h));
        using var brush = new SolidBrush(ColorUtil.Parse(item.ForeColor) ?? Color.Black);
        g.FillPath(brush, path);
    }

    private static void DrawBarcode(Graphics g, RenderedItem item, RectangleF rect)
    {
        using var brush = new SolidBrush(ColorUtil.Parse(item.ForeColor) ?? Color.Black);
        foreach (var (x, w) in BarcodeLayout.Bars(item.BarcodeType, item.Text, rect.Width))
            g.FillRectangle(brush, rect.X + (float)x, rect.Y, (float)w, rect.Height);
    }

    private static void DrawImage(Graphics g, RenderedItem item, RectangleF rect)
    {
        if (item.Image == null || item.Image.Length == 0) return;
        try
        {
            var picture = SvgRaster.Resolve(item.Image, rect.Width, rect.Height, 1);     // an SVG drawing becomes a PNG
            if (picture.Length == 0) return;
            using var ms = new MemoryStream(picture);
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
        if (item.Runs is { Count: > 0 }) { DrawRichText(g, item, rect); return; }
        using var font = CreateFont(item.FontFamily, item.FontSize, item.Bold, item.Italic, item.Underline);
        using var brush = new SolidBrush(ColorUtil.Parse(item.ForeColor) ?? Color.Black);
        using var fmt = CreateFormat(item);
        var inner = RectangleF.Inflate(rect, -2, 0);
        g.DrawString(item.Text, font, brush, inner, fmt);
    }

    /// <summary>
    /// Rich text (requirement 36b): the runs are broken into lines by the shared RichLayout, then every piece is drawn in its own look,
    /// sitting on a common baseline. Without word wrap the lines are clipped to the box, not ellipsised.
    /// </summary>
    private static void DrawRichText(Graphics g, RenderedItem item, RectangleF rect)
    {
        var inner = RectangleF.Inflate(rect, -2, 0);
        var fonts = new Dictionary<TextRun, Font>();
        var brushes = new Dictionary<string, SolidBrush>();
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        var state = g.Save();
        try
        {
            Font FontOf(TextRun run)
            {
                if (fonts.TryGetValue(run, out var cached)) return cached;
                var f = CreateFont(item.FontFamily, item.FontSize, item.Bold || run.Bold, item.Italic || run.Italic, item.Underline || run.Underline);
                if (run.Strike) { var styled = new Font(f, f.Style | FontStyle.Strikeout); f.Dispose(); f = styled; }
                fonts[run] = f;
                return f;
            }
            SolidBrush BrushOf(TextRun run)
            {
                var key = run.Color.Length > 0 ? run.Color : item.ForeColor;
                if (brushes.TryGetValue(key, out var cached)) return cached;
                var b = new SolidBrush(ColorUtil.Parse(key) ?? ColorUtil.Parse(item.ForeColor) ?? Color.Black);
                brushes[key] = b;
                return b;
            }
            // Heights come from the font's design metrics, so they do not depend on the device.
            static double Metric(Font f, Func<FontFamily, FontStyle, int> pick) =>
                pick(f.FontFamily, f.Style) * (double)f.Size / f.FontFamily.GetEmHeight(f.Style);
            double Measure(TextRun run, string text) => g.MeasureString(text, FontOf(run), PointF.Empty, fmt).Width;
            double Height(TextRun run) => Metric(FontOf(run), (ff, st) => ff.GetLineSpacing(st));
            double Ascent(TextRun run) => Metric(FontOf(run), (ff, st) => ff.GetCellAscent(st));

            using var basis = CreateFont(item.FontFamily, item.FontSize, item.Bold, item.Italic, false);
            double emptyHeight = Metric(basis, (ff, st) => ff.GetLineSpacing(st));

            var lines = RichLayout.Wrap(item.Runs!, inner.Width, item.WordWrap, Measure, Height, emptyHeight);
            double total = RichLayout.TotalHeight(lines);
            double y = item.VAlign switch
            {
                VAlign.Middle => inner.Y + (inner.Height - total) / 2,
                VAlign.Bottom => inner.Bottom - total,
                _ => inner.Y
            };

            g.SetClip(rect, CombineMode.Intersect);
            foreach (var line in lines)
            {
                double x = item.HAlign switch
                {
                    HAlign.Center => inner.X + (inner.Width - line.Width) / 2,
                    HAlign.Right => inner.Right - line.Width,
                    _ => inner.X
                };
                double baseline = y + (line.Pieces.Count == 0 ? 0 : line.Pieces.Max(p => Ascent(p.Run)));
                foreach (var piece in line.Pieces)
                {
                    g.DrawString(piece.Text, FontOf(piece.Run), BrushOf(piece.Run), (float)x, (float)(baseline - Ascent(piece.Run)), fmt);
                    x += piece.Width;
                }
                y += line.Height;
            }
        }
        finally
        {
            g.Restore(state);
            foreach (var f in fonts.Values) f.Dispose();
            foreach (var b in brushes.Values) b.Dispose();
        }
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