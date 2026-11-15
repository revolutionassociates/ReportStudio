using System.Globalization;
using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Writes an editable Rich Text Format (.rtf) file that Word, WordPad, LibreOffice and most editors open: the report as a
/// borderless table that keeps the column layout, like the Word export. Text keeps its bold, italic, size, colour,
/// alignment and cell background. The file is plain 7-bit text; characters outside ASCII are written as \u escapes,
/// so every language survives. Pictures, shapes and codes are not written (the table carries text only).
/// </summary>
public static class RtfExporter
{
    private const int TwipsPerPx = 15;   // 1 px at 96 dpi = 15 twips

    public static void Export(ReportResult report, string path) =>
        File.WriteAllText(path, Build(report), Encoding.ASCII);

    /// <summary>Writes to a stream (left open).</summary>
    public static void Export(ReportResult report, Stream stream)
    {
        var bytes = ToBytes(report);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static byte[] ToBytes(ReportResult report) => Encoding.ASCII.GetBytes(Build(report));

    public static string Build(ReportResult report)
    {
        var grid = Grid.Build(report);

        // Colour table: entry 0 is "automatic"; the rest are the colours the cells use, in first-seen order.
        var colours = new List<System.Drawing.Color>();
        int ColourIndex(string text)
        {
            var c = ColorUtil.Parse(text);
            if (c == null) return 0;
            int at = colours.IndexOf(c.Value);
            if (at < 0) { colours.Add(c.Value); at = colours.Count - 1; }
            return at + 1;
        }

        var body = new StringBuilder();
        var edges = new List<int> { 0 };                       // column boundaries in twips
        double sum = 0;
        foreach (var w in grid.ColumnWidths) { sum += w; edges.Add((int)Math.Round(sum * TwipsPerPx)); }

        foreach (var row in grid.Rows)
        {
            var cells = new List<(int From, int To, GridCell? Cell)>();
            int col = 0;
            foreach (var cell in row.Cells.OrderBy(c => c.Column))
            {
                if (cell.Column < col || cell.Column >= grid.ColumnWidths.Count) continue;   // overlapping or outside: skip
                if (cell.Column > col) cells.Add((col, cell.Column, null));                    // gap before it
                int end = Math.Min(cell.Column + Math.Max(1, cell.Span), grid.ColumnWidths.Count);
                cells.Add((cell.Column, end, cell));
                col = end;
            }
            if (col < grid.ColumnWidths.Count) cells.Add((col, grid.ColumnWidths.Count, null));
            if (cells.Count == 0) continue;

            body.Append("\\trowd\\trgaph0\\trleft0");
            foreach (var (_, to, cell) in cells)
            {
                body.Append("\\clvertalc");
                if (cell != null && ColorUtil.Parse(cell.BackColor) != null)
                    body.Append("\\clcbpat").Append(ColourIndex(cell.BackColor));
                body.Append("\\cellx").Append(edges[to].ToString(CultureInfo.InvariantCulture));
            }
            body.Append('\n');
            foreach (var (_, _, cell) in cells)
            {
                body.Append("\\pard\\intbl\\sb20\\sa20");
                if (cell == null) { body.Append("\\cell\n"); continue; }

                body.Append(cell.Align switch { HAlign.Center => "\\qc", HAlign.Right => "\\qr", _ => "\\ql" });
                int fore = ColourIndex(cell.ForeColor);
                int fontHalfPoints = (int)Math.Round((cell.FontSize > 0 ? cell.FontSize : 10) * 2);
                body.Append("\\fs").Append(fontHalfPoints.ToString(CultureInfo.InvariantCulture));
                if (fore > 0) body.Append("\\cf").Append(fore.ToString(CultureInfo.InvariantCulture));
                if (cell.Bold) body.Append("\\b");
                if (cell.Italic) body.Append("\\i");
                body.Append(' ').Append(Escape(cell.Text));
                if (cell.Bold) body.Append("\\b0");
                if (cell.Italic) body.Append("\\i0");
                body.Append("\\cell\n");
            }
            body.Append("\\row\n");
        }

        var first = report.Pages.FirstOrDefault();
        double pageW = first is { Width: > 0 } ? first.Width : 794;
        double pageH = first is { Height: > 0 } ? first.Height : 1123;
        int margin = (int)Math.Max(360, (pageW - sum) / 2 * TwipsPerPx);

        var rtf = new StringBuilder(body.Length + 512);
        rtf.Append("{\\rtf1\\ansi\\ansicpg1252\\uc1\\deff0\n");
        rtf.Append("{\\fonttbl{\\f0\\fswiss\\fcharset0 Arial;}}\n");
        rtf.Append("{\\colortbl;");
        foreach (var c in colours)
            rtf.Append("\\red").Append(c.R).Append("\\green").Append(c.G).Append("\\blue").Append(c.B).Append(';');
        rtf.Append("}\n");
        if (!string.IsNullOrWhiteSpace(report.ReportName))
            rtf.Append("{\\info{\\title ").Append(Escape(report.ReportName)).Append("}}\n");
        rtf.Append("\\paperw").Append(((int)(pageW * TwipsPerPx)).ToString(CultureInfo.InvariantCulture));
        rtf.Append("\\paperh").Append(((int)(pageH * TwipsPerPx)).ToString(CultureInfo.InvariantCulture));
        if (pageW > pageH) rtf.Append("\\landscape");
        rtf.Append("\\margl").Append(margin.ToString(CultureInfo.InvariantCulture))
           .Append("\\margr").Append(margin.ToString(CultureInfo.InvariantCulture))
           .Append("\\margt720\\margb720\\viewkind1\n");
        rtf.Append(body);
        rtf.Append("\\pard\\par\n}");
        return rtf.ToString();
    }

    /// <summary>Text made safe for RTF: \, { and } escaped, line breaks and tabs as control words, everything outside ASCII as \uN?.</summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '{': sb.Append("\\{"); break;
                case '}': sb.Append("\\}"); break;
                case '\t': sb.Append("\\tab "); break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    sb.Append("\\line ");
                    break;
                case '\n': sb.Append("\\line "); break;
                default:
                    if (ch < 32) break;                                  // other control characters are dropped
                    if (ch < 127) sb.Append(ch);
                    else sb.Append("\\u").Append(((short)ch).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
