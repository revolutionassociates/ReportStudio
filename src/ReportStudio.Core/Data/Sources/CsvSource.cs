using System.Text;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>Reads delimited text (RFC 4180: quoted values, doubled quotes, line breaks inside quotes).</summary>
public static class CsvSource
{
    /// <param name="stopAtLimit">True to read only the first <paramref name="maxRows"/> rows (to find the field names); false to refuse a source with more.</param>
    public static List<Row> Read(Stream stream, string? delimiter, bool hasHeader, int maxRows = SourceLimits.MaxRows, bool stopAtLimit = false)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var text = reader.ReadToEnd();
        char sep = Separator(delimiter, text);

        var records = Parse(text, sep, maxRows + (hasHeader ? 1 : 0), stopAtLimit);
        var rows = new List<Row>();
        if (records.Count == 0) return rows;

        List<string> names;
        int first;
        if (hasHeader)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names = records[0].Select((n, i) => ValueParser.FieldName(n, i, used)).ToList();
            first = 1;
        }
        else
        {
            names = Enumerable.Range(1, records.Max(r => r.Count)).Select(i => "Column" + i).ToList();
            first = 0;
        }
        if (names.Count > SourceLimits.MaxColumns) throw new SourceException($"The file has more than {SourceLimits.MaxColumns} columns.");

        for (int r = first; r < records.Count; r++)
        {
            var record = records[r];
            var row = new Row(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < names.Count; c++)
                row[names[c]] = c < record.Count ? ValueParser.Infer(record[c]) : null;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The separator named by the report ("," ";" "|" or "\t" / "tab"), or the most common of , ; tab | in the first line.</summary>
    public static char Separator(string? delimiter, string text)
    {
        var d = (delimiter ?? "").Trim();
        if (d.Equals("\\t", StringComparison.Ordinal) || d.Equals("tab", StringComparison.OrdinalIgnoreCase)) return '\t';
        if (d.Length == 1) return d[0];
        if (d.Length > 1) throw new SourceException("The CSV separator must be one character (or \\t for a tab).");

        int end = text.IndexOfAny(new[] { '\r', '\n' });
        var firstLine = end < 0 ? text : text[..end];
        char best = ',';
        int bestCount = 0;
        foreach (var candidate in new[] { ',', ';', '\t', '|' })
        {
            int count = firstLine.Count(ch => ch == candidate);
            if (count > bestCount) { best = candidate; bestCount = count; }
        }
        return best;
    }

    private static List<List<string>> Parse(string text, char sep, int maxRecords, bool stopAtLimit)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false, done = false;

        void EndField() { record.Add(field.ToString()); field.Clear(); any = true; }
        void EndRecord()
        {
            EndField();
            // A blank line is not a record.
            if (!(record.Count == 1 && record[0].Length == 0)) records.Add(record);
            record = new List<string>();
            any = false;
            if (records.Count > maxRecords) throw new SourceException($"The file has more than {SourceLimits.MaxRows:N0} rows.");
            if (stopAtLimit && records.Count >= maxRecords) done = true;
        }

        for (int i = 0; i < text.Length && !done; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == sep) EndField();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRecord(); }
            else if (c == '\n') EndRecord();
            else field.Append(c);

            if (record.Count > SourceLimits.MaxColumns) throw new SourceException($"The file has more than {SourceLimits.MaxColumns} columns.");
        }
        if (!done && (any || field.Length > 0)) EndRecord();
        return records;
    }
}
