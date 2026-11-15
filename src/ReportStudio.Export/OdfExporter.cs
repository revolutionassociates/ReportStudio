using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Writes OpenDocument files (ISO 26300) that LibreOffice, OpenOffice and Word open: a text document (.odt) and a spreadsheet
/// (.ods). Both carry the report as a borderless table with the same column grid as the Word and Excel exports. Cells keep
/// bold, italic, size, text colour, alignment and background colour. The spreadsheet stores numbers and dates as real values
/// (shown with the printed number of decimals), so they can be summed and sorted; everything else is text.
/// </summary>
public static class OdfExporter
{
    private const string MimeText = "application/vnd.oasis.opendocument.text";
    private const string MimeSheet = "application/vnd.oasis.opendocument.spreadsheet";
    private const string Namespaces =
        " xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"" +
        " xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"" +
        " xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"" +
        " xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"" +
        " xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"" +
        " xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\"" +
        " xmlns:number=\"urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0\"" +
        " xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
        " xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\"";
    private const string FontFaces =
        "<office:font-face-decls><style:font-face style:name=\"Arial\" svg:font-family=\"Arial\"/></office:font-face-decls>";

    public static void ExportText(ReportResult report, string path)
    {
        using var file = File.Create(path);
        ExportText(report, file);
    }

    /// <summary>Writes an .odt to a stream (left open).</summary>
    public static void ExportText(ReportResult report, Stream stream) => Write(report, stream, spreadsheet: false, null);

    public static void ExportSpreadsheet(ReportResult report, string path, CultureInfo? culture = null)
    {
        using var file = File.Create(path);
        ExportSpreadsheet(report, file, culture);
    }

    /// <summary>Writes an .ods to a stream (left open). The culture reads the printed numbers back (default: the current culture).</summary>
    public static void ExportSpreadsheet(ReportResult report, Stream stream, CultureInfo? culture = null) =>
        Write(report, stream, spreadsheet: true, culture ?? CultureInfo.CurrentCulture);

    // ---------------------------------------------------------------------------------------

    private static void Write(ReportResult report, Stream stream, bool spreadsheet, CultureInfo? culture)
    {
        ArgumentNullException.ThrowIfNull(report);
        string mime = spreadsheet ? MimeSheet : MimeText;
        var grid = Grid.Build(report);

        var registry = new Registry();
        var (table, tableWidthPx) = BuildTable(grid, spreadsheet, culture, registry);
        var page = report.Pages.FirstOrDefault();
        double pageW = page is { Width: > 0 } ? page.Width : 794;
        double pageH = page is { Height: > 0 } ? page.Height : 1123;

        string content =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-content" + Namespaces + " office:version=\"1.2\">" +
            FontFaces +
            "<office:automatic-styles>" + registry.Xml + "</office:automatic-styles>" +
            "<office:body>" + (spreadsheet
                ? "<office:spreadsheet>" + table + "</office:spreadsheet>"
                : "<office:text>" + table + "<text:p/></office:text>") +
            "</office:body></office:document-content>";

        double marginPx = Math.Max(24, (pageW - tableWidthPx) / 2);
        string styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-styles" + Namespaces + " office:version=\"1.2\">" +
            FontFaces +
            "<office:automatic-styles><style:page-layout style:name=\"pm1\">" +
            "<style:page-layout-properties fo:page-width=\"" + In(pageW) + "\" fo:page-height=\"" + In(pageH) + "\"" +
            " style:print-orientation=\"" + (pageW > pageH ? "landscape" : "portrait") + "\"" +
            " fo:margin-top=\"0.5in\" fo:margin-bottom=\"0.5in\" fo:margin-left=\"" + In(marginPx) + "\" fo:margin-right=\"" + In(marginPx) + "\"/>" +
            "</style:page-layout></office:automatic-styles>" +
            "<office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/></office:master-styles>" +
            "</office:document-styles>";

        string meta =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-meta" + Namespaces + " office:version=\"1.2\"><office:meta>" +
            (string.IsNullOrWhiteSpace(report.ReportName) ? "" : "<dc:title>" + Esc(report.ReportName) + "</dc:title>") +
            "<meta:generator>ReportStudio</meta:generator></office:meta></office:document-meta>";

        string manifest =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"" + mime + "\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>" +
            "</manifest:manifest>";

        // Built in memory first: a seekable stream lets the zip writer put the sizes in the entry headers, which the
        // "mimetype" entry needs (it must be first, stored without compression, readable at a fixed position).
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "mimetype", mime, CompressionLevel.NoCompression);
            AddEntry(zip, "META-INF/manifest.xml", manifest, CompressionLevel.Optimal);
            AddEntry(zip, "content.xml", content, CompressionLevel.Optimal);
            AddEntry(zip, "styles.xml", styles, CompressionLevel.Optimal);
            AddEntry(zip, "meta.xml", meta, CompressionLevel.Optimal);
        }
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    private static void AddEntry(ZipArchive zip, string name, string text, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>The report rows as lists of cells; gaps between and after cells are filled with empty ones (Cell = null).</summary>
    internal static List<List<(int Start, int End, GridCell? Cell)>> RowsOf(Grid grid)
    {
        var rows = new List<List<(int Start, int End, GridCell? Cell)>>();
        foreach (var row in grid.Rows)
        {
            var cells = new List<(int Start, int End, GridCell? Cell)>();
            int col = 0;
            foreach (var cell in row.Cells.OrderBy(c => c.Column))
            {
                if (cell.Column < col || cell.Column >= grid.ColumnWidths.Count) continue;   // overlapping or outside: skip
                if (cell.Column > col) cells.Add((col, cell.Column, null));
                int end = Math.Min(cell.Column + Math.Max(1, cell.Span), grid.ColumnWidths.Count);
                cells.Add((cell.Column, end, cell));
                col = end;
            }
            if (col < grid.ColumnWidths.Count) cells.Add((col, grid.ColumnWidths.Count, null));
            if (cells.Count > 0) rows.Add(cells);
        }
        return rows;
    }

    private static (string Xml, double WidthPx) BuildTable(Grid grid, bool spreadsheet, CultureInfo? culture, Registry reg)
    {
        double widthPx = grid.ColumnWidths.Sum();
        var sb = new StringBuilder();

        if (grid.Rows.Count == 0)
        {
            // A text document needs no table; a spreadsheet needs a sheet with one empty cell.
            if (!spreadsheet) return ("", 0);
            return ("<table:table table:name=\"Report\"><table:table-column/><table:table-row><table:table-cell/></table:table-row></table:table>", 0);
        }

        string tableStyle = spreadsheet
            ? reg.Get("ta", "sheet", n => "<style:style style:name=\"" + n + "\" style:family=\"table\" style:master-page-name=\"Standard\">" +
                                          "<style:table-properties table:display=\"true\" style:writing-mode=\"lr-tb\"/></style:style>")
            : reg.Get("ta", "text", n => "<style:style style:name=\"" + n + "\" style:family=\"table\">" +
                                         "<style:table-properties style:width=\"" + In(widthPx) + "\" table:align=\"left\"/></style:style>");

        sb.Append("<table:table table:name=\"Report\" table:style-name=\"").Append(tableStyle).Append("\">");
        foreach (var w in grid.ColumnWidths)
        {
            string columnStyle = reg.Get("co", In(w), n => "<style:style style:name=\"" + n + "\" style:family=\"table-column\">" +
                                                           "<style:table-column-properties style:column-width=\"" + In(w) + "\"/></style:style>");
            sb.Append("<table:table-column table:style-name=\"").Append(columnStyle).Append("\"/>");
        }

        foreach (var cells in RowsOf(grid))
        {
            sb.Append("<table:table-row>");
            foreach (var (start, end, cell) in cells)
            {
                int span = end - start;
                if (cell == null) AppendEmpty(sb, spreadsheet, span);
                else AppendCell(sb, cell, span, spreadsheet, culture, reg);
            }
            sb.Append("</table:table-row>");
        }
        sb.Append("</table:table>");
        return (sb.ToString(), widthPx);
    }

    private static void AppendEmpty(StringBuilder sb, bool spreadsheet, int span)
    {
        // One empty cell per column: a text-document table must have the same number of cells in every row.
        for (int i = 0; i < span; i++)
            sb.Append(spreadsheet ? "<table:table-cell/>" : "<table:table-cell><text:p/></table:table-cell>");
    }

    private static void AppendCell(StringBuilder sb, GridCell cell, int span, bool spreadsheet, CultureInfo? culture, Registry reg)
    {
        var back = ColorUtil.Parse(cell.BackColor);
        string valueAttributes = "";
        string dataStyle = "";

        if (spreadsheet && culture != null)
        {
            if (DateTime.TryParseExact(cell.Text.Trim(), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                valueAttributes = " office:value-type=\"date\" office:date-value=\"" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\"";
                dataStyle = reg.Get("D", "dmy", n => "<number:date-style style:name=\"" + n + "\">" +
                    "<number:day number:style=\"long\"/><number:text>-</number:text><number:month number:style=\"long\"/>" +
                    "<number:text>-</number:text><number:year number:style=\"long\"/></number:date-style>");
            }
            else if (IsNumber(cell.Text, culture, out var number))
            {
                int decimals = DecimalsOf(cell.Text, culture);
                bool grouped = cell.Text.Contains(culture.NumberFormat.NumberGroupSeparator, StringComparison.Ordinal);
                valueAttributes = " office:value-type=\"float\" office:value=\"" + number.ToString(CultureInfo.InvariantCulture) + "\"";
                dataStyle = reg.Get("N", decimals.ToString(CultureInfo.InvariantCulture) + (grouped ? "g" : ""), n =>
                    "<number:number-style style:name=\"" + n + "\"><number:number number:decimal-places=\"" + decimals +
                    "\" number:min-decimal-places=\"" + decimals + "\" number:min-integer-digits=\"1\" number:grouping=\"" +
                    (grouped ? "true" : "false") + "\"/></number:number-style>");
            }
        }
        if (valueAttributes.Length == 0 && spreadsheet) valueAttributes = " office:value-type=\"string\"";

        string cellStyle = reg.Get("ce", (back == null ? "" : Hex(back.Value)) + "|" + dataStyle, n =>
            "<style:style style:name=\"" + n + "\" style:family=\"table-cell\"" +
            (dataStyle.Length > 0 ? " style:data-style-name=\"" + dataStyle + "\"" : "") + ">" +
            "<style:table-cell-properties fo:background-color=\"" + (back == null ? "transparent" : Hex(back.Value)) + "\"" +
            " style:vertical-align=\"middle\" fo:padding=\"0.02in\" fo:border=\"none\"/></style:style>");

        string align = cell.Align switch { HAlign.Center => "center", HAlign.Right => "end", _ => "start" };
        string paragraph = reg.Get("pa", align, n => "<style:style style:name=\"" + n + "\" style:family=\"paragraph\">" +
            "<style:paragraph-properties fo:text-align=\"" + align + "\" fo:margin-top=\"0.01in\" fo:margin-bottom=\"0.01in\"/></style:style>");

        double size = cell.FontSize > 0 ? cell.FontSize : 10;
        var fore = ColorUtil.Parse(cell.ForeColor);
        string key = cell.Bold + "|" + cell.Italic + "|" + size.ToString("0.##", CultureInfo.InvariantCulture) + "|" + (fore == null ? "" : Hex(fore.Value));
        string textStyle = reg.Get("tx", key, n => "<style:style style:name=\"" + n + "\" style:family=\"text\"><style:text-properties" +
            " style:font-name=\"Arial\" fo:font-size=\"" + size.ToString("0.##", CultureInfo.InvariantCulture) + "pt\"" +
            (cell.Bold ? " fo:font-weight=\"bold\"" : "") + (cell.Italic ? " fo:font-style=\"italic\"" : "") +
            (fore == null ? "" : " fo:color=\"" + Hex(fore.Value) + "\"") + "/></style:style>");

        sb.Append("<table:table-cell table:style-name=\"").Append(cellStyle).Append('"').Append(valueAttributes);
        if (span > 1) sb.Append(" table:number-columns-spanned=\"").Append(span).Append('"');
        sb.Append("><text:p text:style-name=\"").Append(paragraph).Append("\"><text:span text:style-name=\"").Append(textStyle).Append("\">")
          .Append(Inline(cell.Text)).Append("</text:span></text:p></table:table-cell>");
        for (int i = 1; i < span; i++) sb.Append("<table:covered-table-cell/>");
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Text for inside a paragraph: escaped, with spaces, tabs and line breaks written the way OpenDocument keeps them.</summary>
    internal static string Inline(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        int spaces = 0;
        bool atStart = true;

        void FlushSpaces()
        {
            if (spaces == 0) return;
            if (atStart) sb.Append("<text:s text:c=\"").Append(spaces).Append("\"/>");
            else
            {
                sb.Append(' ');
                if (spaces > 1) sb.Append("<text:s text:c=\"").Append(spaces - 1).Append("\"/>");
            }
            spaces = 0;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == ' ') { spaces++; continue; }
            FlushSpaces();
            atStart = false;
            switch (ch)
            {
                case '\t': sb.Append("<text:tab/>"); break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    sb.Append("<text:line-break/>"); atStart = true; break;
                case '\n': sb.Append("<text:line-break/>"); atStart = true; break;
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default:
                    if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        sb.Append(ch).Append(text[++i]);
                    else if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);   // control characters XML forbids are dropped
                    break;
            }
        }
        FlushSpaces();
        return sb.ToString();
    }

    /// <summary>Text for an attribute value or element content.</summary>
    internal static string Esc(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        sb.Append(ch).Append(text[++i]);
                    else if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool IsNumber(string text, CultureInfo culture, out decimal value)
    {
        value = 0;
        var t = text.Trim();
        if (t.Length == 0) return false;
        // Keep identifiers such as "007" as text.
        if (t.Length > 1 && t[0] == '0' && char.IsDigit(t[1])) return false;
        return decimal.TryParse(t, NumberStyles.Number, culture, out value);
    }

    /// <summary>How many digits follow the decimal separator in the printed number.</summary>
    private static int DecimalsOf(string text, CultureInfo culture)
    {
        var t = text.Trim();
        string separator = culture.NumberFormat.NumberDecimalSeparator;
        int at = t.LastIndexOf(separator, StringComparison.Ordinal);
        if (at < 0) return 0;
        int count = 0;
        for (int i = at + separator.Length; i < t.Length && char.IsDigit(t[i]); i++) count++;
        return count;
    }

    private static string In(double px) => (px / 96.0).ToString("0.####", CultureInfo.InvariantCulture) + "in";

    private static string Hex(System.Drawing.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Hands out one style name per distinct look and keeps the XML of each.</summary>
    private sealed class Registry
    {
        private readonly Dictionary<string, string> _names = new();
        private readonly Dictionary<string, int> _counters = new();
        public StringBuilder Xml { get; } = new();

        public string Get(string prefix, string key, Func<string, string> make)
        {
            string full = prefix + "|" + key;
            if (_names.TryGetValue(full, out var name)) return name;
            _counters.TryGetValue(prefix, out int count);
            _counters[prefix] = ++count;
            name = prefix + count.ToString(CultureInfo.InvariantCulture);
            _names[full] = name;
            Xml.Append(make(name));
            return name;
        }
    }
}
