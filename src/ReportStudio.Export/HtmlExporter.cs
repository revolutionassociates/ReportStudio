using System.Globalization;
using System.Net;
using System.Text;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Writes the report as one self-contained .html file that looks like the printed pages: every page is a sheet with
/// its elements positioned exactly where the layout engine put them. Pictures are embedded (data URIs), barcodes and
/// QR codes are inline SVG, so there are no extra files, scripts or external requests. The file prints one report page
/// per sheet. Text stays selectable; fonts are not embedded, so a machine without Ubuntu falls back to Segoe UI / Arial.
/// </summary>
public static class HtmlExporter
{
    public static void Export(ReportResult report, string path)
    {
        File.WriteAllText(path, Build(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Writes to a stream (left open).</summary>
    public static void Export(ReportResult report, Stream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(Build(report));
    }

    public static byte[] ToBytes(ReportResult report) => new UTF8Encoding(false).GetBytes(Build(report));

    public static string Build(ReportResult report)
    {
        var html = new StringBuilder(16 * 1024);
        var first = report.Pages.FirstOrDefault();
        double pageW = first is { Width: > 0 } ? first.Width : 794;
        double pageH = first is { Height: > 0 } ? first.Height : 1123;

        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(report.ReportName) ? "Report" : report.ReportName)).Append("</title>\n");
        html.Append("<style>\n");
        html.Append("body{margin:0;background:#e5e7eb;font-family:'Ubuntu','Segoe UI',Arial,sans-serif}\n");
        html.Append(".page{position:relative;background:#fff;margin:16px auto;overflow:hidden;box-shadow:0 1px 6px rgba(0,0,0,.25)}\n");
        html.Append(".page>*{position:absolute;box-sizing:border-box}\n");
        html.Append(".t{display:flex;padding:0 2px;line-height:1.25}\n");
        html.Append(".t>div{width:100%}\n");
        html.Append(".nw>div{white-space:pre;overflow:hidden;text-overflow:ellipsis}\n");
        html.Append(".w>div{white-space:pre-wrap;overflow-wrap:anywhere}\n");
        html.Append("svg{display:block}\n");
        html.Append("@page{size:").Append(Px(pageW)).Append("px ").Append(Px(pageH)).Append("px;margin:0}\n");
        html.Append("@media print{body{background:#fff}.page{margin:0;box-shadow:none;break-after:page}.page:last-child{break-after:auto}}\n");
        html.Append("</style>\n</head>\n<body>\n");

        foreach (var page in report.Pages)
        {
            html.Append("<section class=\"page\" style=\"width:").Append(Px(page.Width)).Append("px;height:")
                .Append(Px(page.Height)).Append("px\">\n");
            foreach (var item in page.Items) AppendItem(html, item);
            html.Append("</section>\n");
        }
        if (report.Pages.Count == 0)
            html.Append("<section class=\"page\" style=\"width:").Append(Px(pageW)).Append("px;height:").Append(Px(pageH)).Append("px\"></section>\n");

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    // ---------------------------------------------------------------------------------------

    private static void AppendItem(StringBuilder html, RenderedItem it)
    {
        var back = Css(it.BackColor);
        var border = Css(it.BorderColor);
        string box = $"left:{Px(it.X)}px;top:{Px(it.Y)}px;width:{Px(it.Width)}px;height:{Px(it.Height)}px";
        string borderCss = border != null && it.BorderWidth > 0
            ? $";border:{Px(it.BorderWidth)}px solid {border}" : "";
        string backCss = back != null ? $";background:{back}" : "";

        switch (it.Kind)
        {
            case ItemKind.Line:
            {
                var colour = Css(it.ForeColor) ?? "#000";
                double w = Math.Max(it.Width, 0.01), h = Math.Max(it.Height, 0.01);
                html.Append($"<svg style=\"left:{Px(it.X)}px;top:{Px(it.Y)}px;overflow:visible\" width=\"{Px(w)}\" height=\"{Px(h)}\">")
                    .Append($"<line x1=\"0\" y1=\"0\" x2=\"{Px(it.Width)}\" y2=\"{Px(it.Height)}\" stroke=\"{colour}\" stroke-width=\"{Px(Math.Max(0.5, it.BorderWidth))}\"/></svg>\n");
                return;
            }
            case ItemKind.Rectangle:
            {
                string edge = it.BorderWidth > 0 ? $";border:{Px(it.BorderWidth)}px solid {border ?? Css(it.ForeColor) ?? "#000"}" : "";
                html.Append($"<div style=\"{box}{backCss}{edge}\"></div>\n");
                return;
            }
            case ItemKind.Image:
                AppendImage(html, it, box, backCss, borderCss);
                return;
            case ItemKind.Barcode:
                AppendBarcode(html, it, box, backCss, borderCss);
                return;
            case ItemKind.QrCode:
                AppendQr(html, it, box, backCss, borderCss);
                return;
            case ItemKind.Text:
                AppendText(html, it, box, backCss, borderCss);
                return;
        }
    }

    private static void AppendText(StringBuilder html, RenderedItem it, string box, string backCss, string borderCss)
    {
        if (string.IsNullOrEmpty(it.Text) && backCss.Length == 0 && borderCss.Length == 0) return;

        var style = new StringBuilder(box).Append(backCss).Append(borderCss);
        style.Append(";font-family:").Append(FontStack(it.FontFamily));
        style.Append(";font-size:").Append(Px(it.FontSize * 96.0 / 72.0)).Append("px");
        if (it.Bold) style.Append(";font-weight:700");
        if (it.Italic) style.Append(";font-style:italic");
        if (it.Underline) style.Append(";text-decoration:underline");
        style.Append(";color:").Append(Css(it.ForeColor) ?? "#000");
        style.Append(";align-items:").Append(it.VAlign switch { VAlign.Middle => "center", VAlign.Bottom => "flex-end", _ => "flex-start" });
        style.Append(";text-align:").Append(it.HAlign switch { HAlign.Center => "center", HAlign.Right => "right", _ => "left" });

        html.Append("<div class=\"t ").Append(it.WordWrap ? "w" : "nw").Append("\" style=\"").Append(style).Append("\"><div>")
            .Append(WebUtility.HtmlEncode(it.Text ?? "")).Append("</div></div>\n");
    }

    private static void AppendImage(StringBuilder html, RenderedItem it, string box, string backCss, string borderCss)
    {
        if (it.Image == null || it.Image.Length == 0)
        {
            if (backCss.Length > 0 || borderCss.Length > 0) html.Append($"<div style=\"{box}{backCss}{borderCss}\"></div>\n");
            return;
        }
        html.Append($"<img alt=\"\" style=\"{box}{backCss}{borderCss};object-fit:contain\" src=\"data:{Mime(it.Image)};base64,")
            .Append(Convert.ToBase64String(it.Image)).Append("\">\n");
    }

    private static void AppendBarcode(StringBuilder html, RenderedItem it, string box, string backCss, string borderCss)
    {
        var colour = Css(it.ForeColor) ?? "#000";
        html.Append($"<svg style=\"{box}{backCss}{borderCss}\" viewBox=\"0 0 {Px(it.Width)} {Px(it.Height)}\" shape-rendering=\"crispEdges\">");
        foreach (var (x, w) in BarcodeLayout.Bars(it.BarcodeType, it.Text, it.Width))
            html.Append($"<rect x=\"{Px(x)}\" y=\"0\" width=\"{Px(w)}\" height=\"{Px(it.Height)}\" fill=\"{colour}\"/>");
        html.Append("</svg>\n");
    }

    private static void AppendQr(StringBuilder html, RenderedItem it, string box, string backCss, string borderCss)
    {
        var colour = Css(it.ForeColor) ?? "#000";
        var cells = QrLayout.Rectangles(it.Text, it.QrLevel, it.Width, it.Height);
        html.Append($"<svg style=\"{box}{backCss}{borderCss}\" viewBox=\"0 0 {Px(it.Width)} {Px(it.Height)}\" shape-rendering=\"crispEdges\">");
        if (cells.Count > 0)
        {
            // One path for all modules, so no hairline gaps appear between neighbours.
            html.Append($"<path fill=\"{colour}\" d=\"");
            foreach (var (x, y, w, h) in cells)
                html.Append($"M{Px(x)} {Px(y)}h{Px(w)}v{Px(h)}h-{Px(w)}z");
            html.Append("\"/>");
        }
        html.Append("</svg>\n");
    }

    // ---------------------------------------------------------------------------------------

    private static string Px(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A CSS colour from the report's hex text, or null when empty/transparent.</summary>
    private static string? Css(string? text)
    {
        var c = ColorUtil.Parse(text);
        return c == null ? null : $"#{c.Value.R:x2}{c.Value.G:x2}{c.Value.B:x2}";
    }

    /// <summary>The element's font first, then the defaults. Names with unusual characters are ignored.</summary>
    private static string FontStack(string? family)
    {
        const string defaults = "'Ubuntu','Segoe UI',Arial,sans-serif";
        if (string.IsNullOrWhiteSpace(family) || !family.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-'))
            return defaults;
        return $"'{family.Trim()}',{defaults}";
    }

    private static string Mime(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return "image/jpeg";
        if (data.Length > 3 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
        if (data.Length > 2 && data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";
        return "image/png";
    }
}
