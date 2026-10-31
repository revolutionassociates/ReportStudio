using System.Globalization;
using System.Text;
using ReportStudio.Core.Formatting;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>How the CSV is written.</summary>
public sealed class CsvOptions
{
    /// <summary>Field separator. Use ';' for regions where Excel expects it.</summary>
    public char Separator { get; set; } = ',';

    /// <summary>
    /// True (default): only the column headings (first page header) and the detail lines, which is what other programs
    /// want to read. False: every printed line, including group headers/footers and report totals.
    /// </summary>
    public bool DataOnly { get; set; } = true;

    /// <summary>
    /// True: write numbers and dates exactly as printed (12,34,567.00 and 15-03-2026). Default false: plain numbers
    /// (1234567.00) and ISO dates (2026-03-15), which every importer reads.
    /// </summary>
    public bool AsPrinted { get; set; }

    /// <summary>The culture the report was printed in, used to read the numbers back. Default: Indian formats.</summary>
    public CultureInfo? Culture { get; set; }
}

/// <summary>
/// Writes the report as CSV, one line per printed line, built from the same table as the Excel and Word exports.
/// UTF-8 with a byte-order mark (so Excel shows the rupee sign and other Unicode text correctly), CRLF line ends.
/// Text that a spreadsheet could run as a formula (starts with = or @, or + / - followed by more text) gets a
/// leading apostrophe.
/// </summary>
public static class CsvExporter
{
    public static void Export(ReportResult report, string path, CsvOptions? options = null)
    {
        using var file = File.Create(path);
        Export(report, file, options);
    }

    /// <summary>Writes to a stream (left open).</summary>
    public static void Export(ReportResult report, Stream stream, CsvOptions? options = null)
    {
        options ??= new CsvOptions();
        var culture = options.Culture ?? IndianFormat.Culture;
        var grid = Grid.Build(report);
        int columns = grid.ColumnWidths.Count;

        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 4096, leaveOpen: true)
        {
            NewLine = "\r\n"
        };

        foreach (var row in grid.Rows)
        {
            if (options.DataOnly && row.Band is not (BandKind.PageHeader or BandKind.Detail)) continue;

            var fields = new string[columns];
            Array.Fill(fields, "");
            foreach (var cell in row.Cells)
                if (cell.Column < columns) fields[cell.Column] = Value(cell.Text, culture, options.AsPrinted);

            writer.WriteLine(string.Join(options.Separator, fields.Select(f => Quote(f, options.Separator))));
        }
        writer.Flush();
    }

    /// <summary>The CSV as bytes (for e-mail attachments and services).</summary>
    public static byte[] ToBytes(ReportResult report, CsvOptions? options = null)
    {
        using var ms = new MemoryStream();
        Export(report, ms, options);
        return ms.ToArray();
    }

    private static string Value(string text, CultureInfo culture, bool asPrinted)
    {
        if (!asPrinted)
        {
            var t = text.Trim();
            if (DateTime.TryParseExact(t, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Identifiers such as "007" stay text, exactly as in the Excel export.
            bool leadingZeroId = t.Length > 1 && t[0] == '0' && char.IsDigit(t[1]);
            if (!leadingZeroId && t.Length > 0 && decimal.TryParse(t, NumberStyles.Number, culture, out var number))
                return number.ToString(CultureInfo.InvariantCulture);
        }

        return Neutralise(text);
    }

    /// <summary>Stops a spreadsheet from treating text as a formula (CSV injection).</summary>
    private static string Neutralise(string text)
    {
        if (text.Length == 0) return text;
        char first = text[0];
        bool risky = first is '=' or '@' or '\t' or '\r' || (first is '+' or '-' && text.Length > 1);
        return risky ? "'" + text : text;
    }

    private static string Quote(string field, char separator)
    {
        bool needs = field.Contains(separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        return needs ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
