using System.Net;
using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Web;

/// <summary>
/// The pages of the web viewer around a report: the list, the parameter form, messages and the bar above a report.
/// Every piece of text that came from a report file or a visitor goes through <see cref="E"/>, and the pages carry no script.
/// </summary>
internal static class WebPages
{
    private const string Css =
        "body{margin:0;font-family:'Ubuntu','Segoe UI',Arial,sans-serif;background:#f3f4f6;color:#111827}" +
        "main{max-width:720px;margin:0 auto;padding:24px 16px}" +
        "h1{font-size:22px;margin:0 0 16px}a{color:#1d4ed8}" +
        ".card{background:#fff;border:1px solid #d1d5db;border-radius:6px;padding:16px;margin-bottom:12px}" +
        "ul{list-style:none;padding:0;margin:0}li{padding:8px 0;border-bottom:1px solid #e5e7eb}li:last-child{border:0}" +
        ".small{color:#6b7280;font-size:13px}" +
        "label{display:block;font-weight:600;margin:12px 0 4px}" +
        "input[type=text],input[type=number],input[type=date],select{width:100%;box-sizing:border-box;padding:6px 8px;font:inherit;border:1px solid #9ca3af;border-radius:4px}" +
        "button{margin-top:16px;padding:8px 18px;font:inherit;background:#1d4ed8;color:#fff;border:0;border-radius:4px;cursor:pointer}" +
        ".err{background:#fef2f2;border-color:#fca5a5}";

    private static readonly string[] FontFiles = { "Ubuntu-Regular", "Ubuntu-Bold", "Ubuntu-Italic", "Ubuntu-BoldItalic" };

    public static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Url(string id) => "/r/" + Uri.EscapeDataString(id);

    public static string Layout(string title, string body) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n" +
        $"<title>{E(title)}</title>\n<style>{Css}</style>\n</head>\n<body>\n<main>\n{body}\n</main>\n</body>\n</html>\n";

    public static string ReportList(IReadOnlyList<ReportEntry> reports)
    {
        var body = new StringBuilder("<h1>Reports</h1>\n");
        if (reports.Count == 0)
        {
            body.Append("<div class=\"card\">There are no reports in the reports folder yet.</div>");
        }
        else
        {
            body.Append("<div class=\"card\"><ul>\n");
            foreach (var r in reports)
            {
                body.Append("<li><a href=\"").Append(E(Url(r.Id))).Append("\">").Append(E(r.Title)).Append("</a>");
                if (!string.Equals(r.Title, r.Id, StringComparison.Ordinal)) body.Append(" <span class=\"small\">").Append(E(r.Id)).Append("</span>");
                body.Append("</li>\n");
            }
            body.Append("</ul></div>");
        }
        return Layout("Reports", body.ToString());
    }

    public static string Message(string title, string message, bool error = true) =>
        Layout(title, $"<h1>{E(title)}</h1>\n<div class=\"card{(error ? " err" : "")}\">{E(message)}</div>\n<p><a href=\"/\">All reports</a></p>");

    public static string ParameterForm(string id, ReportDefinition def)
    {
        var title = !string.IsNullOrWhiteSpace(def.Title) ? def.Title : id;
        var body = new StringBuilder();
        body.Append("<h1>").Append(E(title)).Append("</h1>\n<form class=\"card\" method=\"get\" action=\"").Append(E(Url(id) + "/view")).Append("\">\n");

        for (int i = 0; i < def.Parameters.Count; i++)
        {
            var p = def.Parameters[i];
            var field = "p." + p.Name;
            var inputId = "p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var value = p.ResolveDefault();
            var label = string.IsNullOrWhiteSpace(p.Prompt) ? p.Name : p.Prompt;
            var choices = ParameterChoices.Parse(p.Choices);

            if (p.Type == ParameterType.Boolean && !p.MultiValue)
            {
                body.Append("<label for=\"").Append(inputId).Append("\"><input type=\"hidden\" name=\"").Append(E(field)).Append("\" value=\"false\">")
                    .Append("<input type=\"checkbox\" id=\"").Append(inputId).Append("\" name=\"").Append(E(field)).Append("\" value=\"true\"")
                    .Append(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? " checked" : "").Append("> ").Append(E(label)).Append("</label>\n");
                continue;
            }

            body.Append("<label for=\"").Append(inputId).Append("\">").Append(E(label)).Append("</label>\n");
            if (choices.Count > 0)
            {
                var selected = p.MultiValue ? ValueList.Split(value) : new List<string> { value };
                body.Append("<select id=\"").Append(inputId).Append("\" name=\"").Append(E(field)).Append('"').Append(p.MultiValue ? " multiple size=\"6\"" : "").Append(">\n");
                foreach (var c in choices)
                {
                    bool on = selected.Any(s => string.Equals(s, c.Value, StringComparison.OrdinalIgnoreCase));
                    body.Append("<option value=\"").Append(E(c.Value)).Append('"').Append(on ? " selected" : "").Append('>').Append(E(c.Label)).Append("</option>\n");
                }
                body.Append("</select>\n");
            }
            else
            {
                var type = p.MultiValue ? "text" : p.Type switch { ParameterType.Number => "number", ParameterType.Date => "date", _ => "text" };
                body.Append("<input type=\"").Append(type).Append("\" id=\"").Append(inputId).Append("\" name=\"").Append(E(field)).Append('"');
                if (type == "number") body.Append(" step=\"any\"");
                body.Append(" value=\"").Append(E(value)).Append("\" maxlength=\"").Append(ReportService.MaxParameterChars).Append("\">\n");
                if (p.MultiValue) body.Append("<span class=\"small\">Separate several values with |</span>\n");
            }
        }

        body.Append("<button type=\"submit\">Show report</button>\n</form>\n<p><a href=\"/\">All reports</a></p>");
        return Layout(title, body.ToString());
    }

    /// <summary>The @font-face rules for the Ubuntu files served under /fonts, for the faces that exist; empty when none do.</summary>
    public static string FontFaces(string fontsFolder)
    {
        var css = new StringBuilder();
        foreach (var name in FontFiles)
        {
            if (!File.Exists(Path.Combine(fontsFolder, name + ".ttf"))) continue;
            css.Append("@font-face{font-family:'Ubuntu';src:url('/fonts/").Append(name).Append(".ttf');")
                .Append("font-weight:").Append(name.Contains("Bold", StringComparison.Ordinal) ? "700" : "400")
                .Append(";font-style:").Append(name.Contains("Italic", StringComparison.Ordinal) ? "italic" : "normal").Append("}\n");
        }
        return css.ToString();
    }

    /// <summary>
    /// Puts the viewer's bar (a link back, a download link, the warnings) at the top of the HTML the exporter wrote, and the font rules in its head,
    /// so the browser shows the same Ubuntu face the page was measured with. The bar is not printed.
    /// </summary>
    public static string WithToolbar(string reportHtml, string id, ReportResult result, string fontsFolder, string query = "")
    {
        // the download links carry the same parameter values as the page being looked at
        var bar = new StringBuilder("<div class=\"rs-bar\"><a href=\"/\">All reports</a> &middot; <a href=\"")
            .Append(E(Url(id))).Append("\">Change parameters</a> &middot; <details><summary>Download</summary><ul>");
        foreach (var f in ReportDownloads.Formats)
            bar.Append("<li><a href=\"").Append(E(Url(id) + "/download/" + f.Id + query)).Append("\">").Append(E(f.Label)).Append("</a></li>");
        bar.Append("</ul></details>");
        if (result.Warnings.Count > 0)
        {
            bar.Append(" &middot; <details><summary>").Append(result.Warnings.Count).Append(result.Warnings.Count == 1 ? " warning" : " warnings").Append("</summary><ul>");
            foreach (var w in result.Warnings) bar.Append("<li>").Append(E(w)).Append("</li>");
            bar.Append("</ul></details>");
        }
        bar.Append("</div>\n");

        var style = "<style>.rs-bar{position:sticky;top:0;z-index:10;background:#111827;color:#fff;padding:8px 16px;font:14px 'Segoe UI',Arial,sans-serif}" +
                    ".rs-bar a{color:#93c5fd}.rs-bar details{display:inline-block;vertical-align:top}.rs-bar ul{margin:6px 0 0;padding-left:18px}" +
                    "@media print{.rs-bar{display:none}}\n" + FontFaces(fontsFolder) + "</style>\n";

        var html = Insert(reportHtml, "</head>", style, before: true);
        return Insert(html, "<body>\n", bar.ToString(), before: false);
    }

    private static string Insert(string html, string marker, string text, bool before)
    {
        int at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return html;
        return html.Insert(before ? at : at + marker.Length, text);
    }
}
