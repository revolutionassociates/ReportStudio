using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>
/// Reads one sheet of an .xlsx workbook without any library: the file is a zip of XML parts. Formulas give the value Excel last
/// saved; dates are recognised from the cell's number format. A part that unpacks to more than the limit is refused (a "zip bomb"),
/// and no DTD or outside file is ever read.
/// </summary>
public static class ExcelSource
{
    private static readonly HashSet<int> BuiltInDateFormats = new(Enumerable.Range(14, 9).Concat(Enumerable.Range(27, 10)).Concat(new[] { 45, 46, 47 }).Concat(Enumerable.Range(50, 9)));

    public static List<Row> Read(Stream stream, string? sheetName, bool hasHeader, int maxRows = SourceLimits.MaxRows, bool stopAtLimit = false)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new SourceException("The file is not an Excel workbook (.xlsx).", ex); }

        using (zip)
        {
            long budget = SourceLimits.MaxUnpackedBytes;
            XmlReader Open(string name, bool required)
            {
                var entry = zip.GetEntry(name.TrimStart('/'));
                if (entry == null)
                    return required ? throw new SourceException($"The workbook has no part '{name}'.") : null!;
                if (entry.Length > budget) throw new SourceException("The workbook unpacks to more than the allowed size.");
                budget -= entry.Length;
                return XmlReader.Create(new LimitedStream(entry.Open(), entry.Length + 1024), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true
                });
            }

            var (sheets, date1904) = ReadWorkbook(Open("xl/workbook.xml", true));
            var relationships = ReadRelationships(Open("xl/_rels/workbook.xml.rels", true));

            var sheet = string.IsNullOrWhiteSpace(sheetName)
                ? sheets.FirstOrDefault()
                : sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (sheet == default)
                throw new SourceException(string.IsNullOrWhiteSpace(sheetName)
                    ? "The workbook has no sheets."
                    : $"The workbook has no sheet '{sheetName}'. Sheets: {string.Join(", ", sheets.Take(10).Select(s => s.Name))}.");
            if (!relationships.TryGetValue(sheet.RelationId, out var target))
                throw new SourceException($"The sheet '{sheet.Name}' cannot be found in the workbook.");
            var partName = target.StartsWith('/') ? target : "xl/" + target;

            var shared = new List<string>();
            using (var reader = Open("xl/sharedStrings.xml", false))
                if (reader != null) shared = ReadSharedStrings(reader);
            var dateStyles = new List<bool>();
            using (var reader = Open("xl/styles.xml", false))
                if (reader != null) dateStyles = ReadDateStyles(reader);

            using var sheetReader = Open(partName, true);
            return ReadSheet(sheetReader, shared, dateStyles, date1904, hasHeader, maxRows, stopAtLimit);
        }
    }

    /// <summary>The names of the sheets, for the designer's list.</summary>
    public static List<string> SheetNames(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("xl/workbook.xml") ?? throw new SourceException("The file is not an Excel workbook (.xlsx).");
            using var reader = XmlReader.Create(new LimitedStream(entry.Open(), Math.Min(entry.Length, SourceLimits.MaxUnpackedBytes) + 1024),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return ReadWorkbook(reader).Sheets.Select(s => s.Name).ToList();
        }
        catch (InvalidDataException ex) { throw new SourceException("The file is not an Excel workbook (.xlsx).", ex); }
    }

    private readonly record struct SheetRef(string Name, string RelationId);

    private static (List<SheetRef> Sheets, bool Date1904) ReadWorkbook(XmlReader reader)
    {
        using (reader)
        {
            var sheets = new List<SheetRef>();
            bool date1904 = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "workbookPr") date1904 = reader.GetAttribute("date1904") is "1" or "true";
                else if (reader.LocalName == "sheet")
                    sheets.Add(new SheetRef(reader.GetAttribute("name") ?? "", reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? ""));
            }
            return (sheets, date1904);
        }
    }

    private static Dictionary<string, string> ReadRelationships(XmlReader reader)
    {
        using (reader)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                    map[id] = target;
            return map;
        }
    }

    private static List<string> ReadSharedStrings(XmlReader reader)
    {
        var strings = new List<string>();
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                var si = (XElement)XNode.ReadFrom(reader);                    // reads the whole <si> and moves past it
                strings.Add(ValueParser.Cut(TextOf(si)));
                if (strings.Count > 5_000_000) throw new SourceException("The workbook has too many distinct texts.");
            }
            else reader.Read();
        }
        return strings;
    }

    /// <summary>The text of a shared string or inline string: its runs joined, without the phonetic (furigana) text.</summary>
    private static string TextOf(XElement element) =>
        string.Concat(element.Descendants().Where(t => t.Name.LocalName == "t" && !t.Ancestors().Any(a => a.Name.LocalName == "rPh")).Select(t => t.Value));

    private static List<bool> ReadDateStyles(XmlReader reader)
    {
        var customDate = new Dictionary<int, bool>();
        var result = new List<bool>();
        bool inCellXfs = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "numFmt" && int.TryParse(reader.GetAttribute("numFmtId"), out var id))
                    customDate[id] = LooksLikeDate(reader.GetAttribute("formatCode") ?? "");
                else if (reader.LocalName == "cellXfs") inCellXfs = true;
                else if (reader.LocalName == "xf" && inCellXfs)
                {
                    int.TryParse(reader.GetAttribute("numFmtId"), out var fmt);
                    result.Add(BuiltInDateFormats.Contains(fmt) || (customDate.TryGetValue(fmt, out var isDate) && isDate));
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs") inCellXfs = false;
        }
        return result;
    }

    private static bool LooksLikeDate(string code)
    {
        var plain = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
        return !plain.Equals("General", StringComparison.OrdinalIgnoreCase) && plain.Any(c => "ymdhsYMDHS".Contains(c));
    }

    private static List<Row> ReadSheet(XmlReader reader, List<string> shared, List<bool> dateStyles, bool date1904,
        bool hasHeader, int maxRows, bool stopAtLimit)
    {
        var grid = new List<object?[]>();
        int limit = maxRows + (hasHeader ? 1 : 0);
        int widest = 0;

        reader.MoveToContent();
        while (!reader.EOF && !(stopAtLimit && grid.Count >= limit))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") { reader.Read(); continue; }

            var rowElement = (XElement)XNode.ReadFrom(reader);               // one row at a time, so a big sheet is never all in memory as XML
            var cells = new SortedDictionary<int, object?>();
            int nextColumn = 0;
            foreach (var c in rowElement.Elements().Where(e => e.Name.LocalName == "c"))
            {
                int column = ColumnIndex((string?)c.Attribute("r"), nextColumn);
                nextColumn = column + 1;
                if (column >= SourceLimits.MaxColumns) throw new SourceException($"The sheet has more than {SourceLimits.MaxColumns} columns.");

                string? type = (string?)c.Attribute("t");
                bool isDateStyle = int.TryParse((string?)c.Attribute("s"), out var style) && style >= 0 && style < dateStyles.Count && dateStyles[style];
                string? value = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                var inline = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                if (inline != null) value = TextOf(inline);

                var cell = CellValue(type, value, isDateStyle, date1904, shared);
                if (cell != null) cells[column] = cell;
            }

            if (cells.Count == 0) continue;                                 // a blank row
            var values = new object?[cells.Keys.Max() + 1];
            foreach (var (column, v) in cells) values[column] = v;
            widest = Math.Max(widest, values.Length);
            grid.Add(values);
            if (grid.Count > limit && !stopAtLimit) throw new SourceException($"The sheet has more than {SourceLimits.MaxRows:N0} rows.");
        }

        var rows = new List<Row>();
        if (grid.Count == 0) return rows;

        List<string> names;
        int first;
        if (hasHeader)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names = Enumerable.Range(0, grid[0].Length)
                .Select(i => ValueParser.FieldName(Convert.ToString(grid[0][i], CultureInfo.InvariantCulture), i, used)).ToList();
            for (int i = names.Count; i < widest; i++) names.Add(ValueParser.FieldName(null, i, used));   // columns right of the header's last cell
            first = 1;
        }
        else
        {
            names = Enumerable.Range(1, widest).Select(i => "Column" + i).ToList();
            first = 0;
        }

        for (int r = first; r < grid.Count && rows.Count < maxRows; r++)
        {
            var row = new Row(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < names.Count; c++) row[names[c]] = c < grid[r].Length ? grid[r][c] : null;
            rows.Add(row);
        }
        return rows;
    }

    private static object? CellValue(string? type, string? value, bool isDateStyle, bool date1904, List<string> shared)
    {
        if (value == null) return null;
        switch (type)
        {
            case "s":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < shared.Count ? shared[index] : null;
            case "str":
            case "inlineStr":
                return ValueParser.Cut(value);
            case "b":
                return value == "1";
            case "e":
                return null;
            case "d":
                return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso) ? iso : ValueParser.Cut(value);
        }
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return ValueParser.Cut(value);
        if (isDateStyle)
        {
            double serial = (double)number + (date1904 ? 1462 : 0);
            if (serial >= 1 && serial < 2958466)
                try { return DateTime.FromOADate(serial); } catch (ArgumentException) { /* out of range: keep the number */ }
        }
        return number;
    }

    /// <summary>"C7" is column 2 (counted from 0). Without a reference the cell follows the one before.</summary>
    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        int column = 0, i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
            column = checked(column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1));
        return i == 0 ? fallback : column - 1;
    }

    /// <summary>Reads at most a set number of bytes, then fails: a part that is bigger than it said it would be is not trusted.</summary>
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private long _left;
        public LimitedStream(Stream inner, long limit) { _inner = inner; _left = limit; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _left + 1));
            _left -= n;
            if (_left < 0) throw new SourceException("A part of the workbook is bigger than it says it is.");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
