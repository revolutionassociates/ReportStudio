using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export.Pdf;

namespace ReportStudio.Export;

/// <summary>
/// Vector PDF output (selectable text), written by ReportStudio's own PDF writer (see the Pdf folder).
/// Page size matches the report page. Fonts used are embedded as subsets.
/// </summary>
public static class PdfExporter
{
    public static void Export(ReportResult report, string path, PdfOptions? options = null)
    {
        using var stream = File.Create(path);
        Export(report, stream, options);
    }

    public static void Export(ReportResult report, Stream stream, PdfOptions? options = null)
    {
        var doc = new PdfDocument(report.ReportName);
        foreach (var page in report.Pages)
        {
            var p = doc.AddPage(page.Width, page.Height);
            foreach (var item in page.Items)
            {
                DrawItem(doc, p, item);
                if (item.Bookmark.Length > 0) doc.Bookmark(item.Bookmark, doc.PageCount - 1, item.Y);
                AddLink(p, item);
            }
        }
        foreach (var entry in report.FindOutline()) doc.Outline(entry.Title, entry.Level, entry.PageIndex, entry.Y);
        doc.Save(stream, options);
    }

    /// <summary>Makes the item's box clickable: a web address, mail or phone link, or a jump to a bookmark.</summary>
    private static void AddLink(PdfPage page, RenderedItem it)
    {
        var (kind, value) = LinkTarget.Parse(it.Link);   // checked again here: a RenderedItem can come from anywhere
        if (kind == LinkKind.External) page.LinkToUri(it.X, it.Y, it.Width, it.Height, value);
        else if (kind == LinkKind.Internal) page.LinkToBookmark(it.X, it.Y, it.Width, it.Height, value.Substring(1));
    }

    private static void DrawItem(PdfDocument doc, PdfPage g, RenderedItem it)
    {
        var back = ColorUtil.Parse(it.BackColor);
        var border = ColorUtil.Parse(it.BorderColor);
        var ink = ColorUtil.Parse(it.ForeColor) ?? Color.Black;

        switch (it.Kind)
        {
            case ItemKind.Line:
                g.Line(it.X, it.Y, it.X + it.Width, it.Y + it.Height, ink, Math.Max(0.5, it.BorderWidth));
                return;
            case ItemKind.Rectangle:
                if (back != null) g.FillRectangle(it.X, it.Y, it.Width, it.Height, back.Value);
                if (it.BorderWidth > 0)
                    g.StrokeRectangle(it.X, it.Y, it.Width, it.Height, border ?? ink, it.BorderWidth);
                return;
            case ItemKind.Image:
                DrawImage(doc, g, it);
                break;
            case ItemKind.QrCode:
            {
                if (back != null) g.FillRectangle(it.X, it.Y, it.Width, it.Height, back.Value);
                var cells = QrLayout.Rectangles(it.Text, it.QrLevel, it.Width, it.Height);
                if (cells.Count == 0) break;

                // One filled path so viewers do not show hairline gaps between modules.
                var shapes = new List<IReadOnlyList<PointF>>();
                foreach (var (x, y, w, h) in cells)
                {
                    float left = (float)(it.X + x), top = (float)(it.Y + y), right = (float)(it.X + x + w), bottom = (float)(it.Y + y + h);
                    shapes.Add(new[] { new PointF(left, top), new PointF(right, top), new PointF(right, bottom), new PointF(left, bottom) });
                }
                g.FillPolygons(shapes, ink, evenOdd: false);
                break;
            }
            case ItemKind.Barcode:
            {
                if (back != null) g.FillRectangle(it.X, it.Y, it.Width, it.Height, back.Value);
                foreach (var (x, w) in BarcodeLayout.Bars(it.BarcodeType, it.Text, it.Width))
                    g.FillRectangle(it.X + x, it.Y, w, it.Height, ink);
                break;
            }
            case ItemKind.Text:
                if (back != null) g.FillRectangle(it.X, it.Y, it.Width, it.Height, back.Value);
                DrawText(doc, g, it);
                break;
        }

        if (border != null && it.BorderWidth > 0)
            g.StrokeRectangle(it.X, it.Y, it.Width, it.Height, border.Value, it.BorderWidth);
    }

    /// <summary>The picture, as large as fits the box with its proportions kept, centred.</summary>
    private static void DrawImage(PdfDocument doc, PdfPage g, RenderedItem it)
    {
        if (it.Image == null || it.Image.Length == 0) return;
        try
        {
            using var bitmap = new Bitmap(new MemoryStream(it.Image));
            double ratio = Math.Min(it.Width / bitmap.Width, it.Height / bitmap.Height);
            double w = bitmap.Width * ratio, h = bitmap.Height * ratio;

            var key = Convert.ToHexString(SHA256.HashData(it.Image));
            var name = doc.Image(key, () => new Bitmap(bitmap));
            g.Image(name, it.X + (it.Width - w) / 2, it.Y + (it.Height - h) / 2, w, h);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ExternalException)
        {
            // unreadable image: leave the box empty
        }
    }

    private static void DrawText(PdfDocument doc, PdfPage g, RenderedItem it)
    {
        if (string.IsNullOrEmpty(it.Text)) return;
        var rect = new RectangleF((float)it.X, (float)it.Y, (float)it.Width, (float)it.Height);
        if (PdfComplexText.TryDraw(g, it, rect)) return;   // Hindi, Tamil, ...: shaped by GDI+, see PdfComplexText

        var face = PdfFontProvider.Resolve(it.FontFamily, it.Bold, it.Italic);
        var font = doc.Font(face);

        // Font size is in points; the page is drawn in report px, so convert pt -> px.
        double size = it.FontSize * 96.0 / 72.0;
        var brush = ColorUtil.Parse(it.ForeColor) ?? Color.Black;

        double innerX = it.X + 2, innerWidth = Math.Max(1, it.Width - 4);
        double innerBottom = it.Y + it.Height;
        var text = Clean(it.Text);

        var lines = it.WordWrap ? Wrap(face, text, size, innerWidth) : text.Split('\n').ToList();
        double lineHeight = face.LineHeight(size);
        double total = lines.Count * lineHeight;
        double top = it.VAlign switch
        {
            VAlign.Middle => it.Y + (it.Height - total) / 2,
            VAlign.Bottom => innerBottom - total,
            _ => it.Y
        };

        foreach (var raw in lines)
        {
            var line = it.WordWrap ? raw : Ellipsize(face, raw, size, innerWidth);
            double width = face.Width(line, size);
            double x = it.HAlign switch
            {
                HAlign.Center => innerX + (innerWidth - width) / 2,
                HAlign.Right => innerX + innerWidth - width,
                _ => innerX
            };
            double baseline = top + face.BaselineOffset(size);
            g.Text(font, size, x, baseline, line, brush);
            if (it.Underline && line.Length > 0)
                g.Line(x, baseline + size * 0.12, x + width, baseline + size * 0.12, brush, Math.Max(0.5, size / 18));
            top += lineHeight;
        }
    }

    /// <summary>Line breaks become \n, tabs become spaces, other control characters are dropped.</summary>
    private static string Clean(string text)
    {
        var chars = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ')
            .Where(ch => ch == '\n' || !char.IsControl(ch));
        return new string(chars.ToArray());
    }

    /// <summary>Greedy word wrap using real glyph metrics.</summary>
    private static List<string> Wrap(TrueTypeFont font, string text, double size, double maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && font.Width(candidate, size) > maxWidth)
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

    private static string Ellipsize(TrueTypeFont font, string text, double size, double maxWidth)
    {
        if (font.Width(text, size) <= maxWidth) return text;
        while (text.Length > 1 && font.Width(text + "…", size) > maxWidth)
            text = text.Substring(0, text.Length - 1);
        return text + "…";
    }
}
