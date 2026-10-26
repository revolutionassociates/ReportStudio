using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>Vector PDF output (selectable text) built with PDFsharp. Page size matches the report page.</summary>
public static class PdfExporter
{
    private const double PxToPt = 72.0 / 96.0;

    static PdfExporter()
    {
        // Fonts from the application's "Fonts" folder (Ubuntu by default) are read as files and embedded in the PDF.
        // Without any, PDFsharp 6 core uses the installed Windows fonts.
        FontCatalog.TryInstallPdfResolver();
        if (GlobalFontSettings.FontResolver == null && OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public static void Export(ReportResult report, string path)
    {
        using var doc = Build(report);
        doc.Save(path);
    }

    public static void Export(ReportResult report, Stream stream)
    {
        using var doc = Build(report);
        doc.Save(stream, closeStream: false);
    }

    private static PdfDocument Build(ReportResult report)
    {
        var doc = new PdfDocument();
        doc.Info.Title = report.ReportName;

        foreach (var page in report.Pages)
        {
            var p = doc.AddPage();
            p.Width = new XUnitPt(page.Width * PxToPt);
            p.Height = new XUnitPt(page.Height * PxToPt);
            using var gfx = XGraphics.FromPdfPage(p);
            gfx.ScaleTransform(PxToPt);          // draw in report pixels
            foreach (var item in page.Items) DrawItem(gfx, item);
        }
        if (doc.PageCount == 0) doc.AddPage();
        return doc;
    }

    private static void DrawItem(XGraphics g, RenderedItem it)
    {
        var rect = new XRect(it.X, it.Y, it.Width, it.Height);
        var back = ColorUtil.Parse(it.BackColor);
        var border = ColorUtil.Parse(it.BorderColor);

        switch (it.Kind)
        {
            case ItemKind.Line:
            {
                var pen = new XPen(ToX(ColorUtil.Parse(it.ForeColor) ?? System.Drawing.Color.Black), Math.Max(0.5, it.BorderWidth));
                g.DrawLine(pen, it.X, it.Y, it.X + it.Width, it.Y + it.Height);
                return;
            }
            case ItemKind.Rectangle:
                if (back != null) g.DrawRectangle(new XSolidBrush(ToX(back.Value)), rect);
                if (it.BorderWidth > 0)
                    g.DrawRectangle(new XPen(ToX(border ?? ColorUtil.Parse(it.ForeColor) ?? System.Drawing.Color.Black), it.BorderWidth), rect);
                return;
            case ItemKind.Image:
                DrawImage(g, it, rect);
                break;
            case ItemKind.Text:
                if (back != null) g.DrawRectangle(new XSolidBrush(ToX(back.Value)), rect);
                DrawText(g, it, rect);
                break;
        }

        if (border != null && it.BorderWidth > 0)
            g.DrawRectangle(new XPen(ToX(border.Value), it.BorderWidth), rect);
    }

    private static void DrawImage(XGraphics g, RenderedItem it, XRect rect)
    {
        if (it.Image == null || it.Image.Length == 0) return;
        try
        {
            using var img = XImage.FromStream(new MemoryStream(it.Image));
            double ratio = Math.Min(rect.Width / img.PixelWidth, rect.Height / img.PixelHeight);
            double w = img.PixelWidth * ratio, h = img.PixelHeight * ratio;
            g.DrawImage(img, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // unreadable image: leave the box empty
        }
    }

    private static void DrawText(XGraphics g, RenderedItem it, XRect rect)
    {
        if (string.IsNullOrEmpty(it.Text)) return;

        var style = XFontStyleEx.Regular;
        if (it.Bold) style |= XFontStyleEx.Bold;
        if (it.Italic) style |= XFontStyleEx.Italic;
        if (it.Underline) style |= XFontStyleEx.Underline;

        // Font size is in points; the graphics unit here is report px, so convert pt -> px.
        double sizePx = it.FontSize * 96.0 / 72.0;
        XFont font;
        try { font = new XFont(it.FontFamily, sizePx, style); }
        catch (Exception) { font = new XFont("Arial", sizePx, style); }

        var brush = new XSolidBrush(ToX(ColorUtil.Parse(it.ForeColor) ?? System.Drawing.Color.Black));
        var inner = new XRect(rect.X + 2, rect.Y, Math.Max(1, rect.Width - 4), rect.Height);

        var lines = it.WordWrap ? Wrap(g, it.Text, font, inner.Width) : it.Text.Replace("\r", "").Split('\n').ToList();
        double lineH = font.GetHeight();
        double total = lines.Count * lineH;
        double y = it.VAlign switch
        {
            VAlign.Middle => inner.Y + (inner.Height - total) / 2,
            VAlign.Bottom => inner.Bottom - total,
            _ => inner.Y
        };

        foreach (var raw in lines)
        {
            var line = it.WordWrap ? raw : Ellipsize(g, raw, font, inner.Width);
            double w = g.MeasureString(line, font).Width;
            double x = it.HAlign switch
            {
                HAlign.Center => inner.X + (inner.Width - w) / 2,
                HAlign.Right => inner.Right - w,
                _ => inner.X
            };
            g.DrawString(line, font, brush, new XPoint(x, y), XStringFormats.TopLeft);
            y += lineH;
        }
    }

    /// <summary>Greedy word wrap using real glyph metrics.</summary>
    private static List<string> Wrap(XGraphics g, string text, XFont font, double maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && g.MeasureString(candidate, font).Width > maxWidth)
                {
                    result.Add(line);
                    line = word;
                }
                else line = candidate;
            }
            result.Add(line);
        }
        return result;
    }

    private static string Ellipsize(XGraphics g, string text, XFont font, double maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;
        while (text.Length > 1 && g.MeasureString(text + "…", font).Width > maxWidth)
            text = text.Substring(0, text.Length - 1);
        return text + "…";
    }

    private static XColor ToX(System.Drawing.Color c) => XColor.FromArgb(c.A, c.R, c.G, c.B);
}
