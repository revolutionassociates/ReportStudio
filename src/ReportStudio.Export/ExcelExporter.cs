using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>What the Excel export does beyond the plain table.</summary>
public sealed class ExcelOptions
{
    /// <summary>Totals such as {Sum(Amount)} in group and report footers become live formulas (=SUM(D5:D9)) when they can reproduce the printed number.</summary>
    public bool Formulas { get; set; } = true;

    /// <summary>
    /// 0 = everything on one sheet. N = one sheet for each instance of group N (1 = the outermost group, e.g. one sheet per country).
    /// Ignored when the report has fewer groups than that.
    /// </summary>
    public int SheetPerGroupLevel { get; set; }
}

/// <summary>Writes an editable .xlsx: one worksheet row per report line, numbers stored as real numbers, totals as formulas.</summary>
public static class ExcelExporter
{
    public static void Export(ReportResult report, string path, CultureInfo? culture = null, ExcelOptions? options = null)
    {
        using var file = File.Create(path);
        Export(report, file, culture, options);
    }

    /// <summary>Writes the workbook to a stream (the stream is left open and must be seekable).</summary>
    public static void Export(ReportResult report, Stream stream, CultureInfo? culture = null, ExcelOptions? options = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        options ??= new ExcelOptions();
        var grid = Grid.Build(report);
        var plan = PlanSheets(grid, report.ReportName, options.SheetPerGroupLevel);
        var formulas = options.Formulas ? new ExcelFormulas(grid, plan.SheetOf, plan.RowNumber, plan.Names, culture) : null;

        using var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();

        var styles = new StyleBook();
        var sheetDatas = new List<SheetData>();
        var sheetMerges = new List<List<string>>();
        for (int s = 0; s < plan.Names.Count; s++)
        {
            sheetDatas.Add(new SheetData());
            sheetMerges.Add(new List<string>());
        }

        for (int i = 0; i < grid.Rows.Count; i++)
        {
            var row = grid.Rows[i];
            uint r = (uint)plan.RowNumber[i];
            var merges = sheetMerges[plan.SheetOf[i]];
            var xr = new Row { RowIndex = r };
            foreach (var cell in row.Cells)
            {
                string reference = ColumnName(cell.Column + 1) + r;
                var xc = new Cell { CellReference = reference };
                string? numberFormat = null;

                if (DateTime.TryParseExact(cell.Text.Trim(), "dd-MM-yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                {
                    xc.DataType = CellValues.Number;
                    xc.CellValue = new CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture));
                    numberFormat = "dd-mm-yyyy";
                    AddFormula(xc, formulas, i, cell);
                }
                else if (IsNumber(cell.Text, culture, out var number))
                {
                    xc.DataType = CellValues.Number;
                    xc.CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture));
                    numberFormat = NumberFormatFor(cell.Text, culture);
                    AddFormula(xc, formulas, i, cell);
                }
                else
                {
                    xc.DataType = CellValues.InlineString;
                    xc.InlineString = new InlineString(new Text(cell.Text) { Space = SpaceProcessingModeValues.Preserve });
                }
                xc.StyleIndex = styles.Get(cell, numberFormat);
                xr.Append(xc);

                if (cell.Span > 1)
                    merges.Add($"{reference}:{ColumnName(cell.Column + cell.Span)}{r}");
            }
            sheetDatas[plan.SheetOf[i]].Append(xr);
        }

        var sheets = new Sheets();
        for (int s = 0; s < plan.Names.Count; s++)
        {
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var columns = new Columns();
            for (int c = 0; c < grid.ColumnWidths.Count; c++)
                columns.Append(new Column
                {
                    Min = (uint)(c + 1), Max = (uint)(c + 1),
                    Width = Math.Round(grid.ColumnWidths[c] / 7.0 + 1, 1), CustomWidth = true
                });

            var worksheet = new Worksheet();
            if (columns.HasChildren) worksheet.Append(columns);
            worksheet.Append(sheetDatas[s]);
            if (sheetMerges[s].Count > 0)
            {
                var mc = new MergeCells();
                foreach (var m in sheetMerges[s]) mc.Append(new MergeCell { Reference = m });
                worksheet.Append(mc);
            }
            wsPart.Worksheet = worksheet;

            sheets.Append(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = (uint)(s + 1),
                Name = plan.Names[s]
            });
        }

        var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = styles.Build();

        wbPart.Workbook.AppendChild(sheets);
        wbPart.Workbook.Save();
    }

    /// <summary>Makes a total cell a live formula when Excel can reproduce the printed number; its cached value stays the printed one.</summary>
    private static void AddFormula(Cell xc, ExcelFormulas? formulas, int rowIndex, GridCell cell)
    {
        var formula = formulas?.For(rowIndex, cell);
        if (formula != null) xc.InsertAt(new CellFormula(formula), 0);   // the formula comes before the cached value
    }

    /// <summary>Which sheet and row each grid row lands on, and the sheets' names.</summary>
    internal sealed class SheetPlan
    {
        public int[] SheetOf { get; init; } = Array.Empty<int>();
        public int[] RowNumber { get; init; } = Array.Empty<int>();
        public List<string> Names { get; init; } = new();
    }

    internal static SheetPlan PlanSheets(Grid grid, string reportName, int level)
    {
        int count = grid.Rows.Count;
        var sheetOf = new int[count];
        var rowNumber = new int[count];

        var keys = new int?[count];
        if (level > 0)
            for (int i = 0; i < count; i++)
                if (grid.Rows[i].GroupIds.Length >= level) keys[i] = grid.Rows[i].GroupIds[level - 1];

        if (!keys.Any(k => k != null))
        {
            // No split: one sheet named after the report.
            for (int i = 0; i < count; i++) rowNumber[i] = i + 1;
            return new SheetPlan { SheetOf = sheetOf, RowNumber = rowNumber, Names = new List<string> { SheetName(reportName) } };
        }

        // Rows above the first group (report header) and the headers of outer groups join the sheet that follows them;
        // footers and the report footer join the sheet before them.
        int? next = null;
        for (int i = count - 1; i >= 0; i--)
        {
            if (keys[i] != null) next = keys[i];
            else if (grid.Rows[i].Band is BandKind.ReportHeader or BandKind.PageHeader or BandKind.GroupHeader) keys[i] = next;
        }
        int? previous = null;
        for (int i = 0; i < count; i++)
        {
            if (keys[i] != null) previous = keys[i];
            else keys[i] = previous;
        }
        int firstKey = keys.First(k => k != null)!.Value;

        var order = new List<int>();
        var names = new List<string>();
        var counters = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int key = keys[i] ?? firstKey;
            int sheet = order.IndexOf(key);
            if (sheet < 0)
            {
                order.Add(key);
                counters.Add(0);
                sheet = order.Count - 1;
                names.Add(UniqueSheetName(KeyText(grid, level, key, sheet), names));
            }
            sheetOf[i] = sheet;
            rowNumber[i] = ++counters[sheet];
        }
        return new SheetPlan { SheetOf = sheetOf, RowNumber = rowNumber, Names = names };
    }

    private static string KeyText(Grid grid, int level, int key, int sheet)
    {
        foreach (var row in grid.Rows)
            if (row.GroupIds.Length >= level && row.GroupIds[level - 1] == key)
            {
                var text = row.GroupPath[level - 1];
                return text.Trim().Length == 0 ? "(blank)" : text;
            }
        return "Group " + (sheet + 1);
    }

    private static string UniqueSheetName(string wanted, List<string> taken)
    {
        string name = SheetName(wanted);
        if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = $" ({n})";
            string candidate = (name.Length + suffix.Length > 31 ? name.Substring(0, 31 - suffix.Length) : name) + suffix;
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
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

    /// <summary>
    /// Excel format code matching how the number was printed: same decimals, and lakh/crore grouping when the
    /// text was grouped (the conditional sections give 12,34,567.00 whatever the viewer's Windows region is).
    /// </summary>
    internal static string NumberFormatFor(string text, CultureInfo culture)
    {
        var nf = culture.NumberFormat;
        var t = text.Trim();

        int point = t.IndexOf(nf.NumberDecimalSeparator, StringComparison.Ordinal);
        int decimals = point < 0 ? 0 : t.Length - point - nf.NumberDecimalSeparator.Length;
        var fraction = decimals > 0 ? "." + new string('0', decimals) : "";

        if (!t.Contains(nf.NumberGroupSeparator, StringComparison.Ordinal))
            return "0" + fraction;

        return $"[>=10000000]##\\,##\\,##\\,##0{fraction};[>=100000]##\\,##\\,##0{fraction};##,##0{fraction}";
    }

    private static string SheetName(string name)
    {
        var bad = new[] { '\\', '/', '?', '*', '[', ']', ':' };
        var clean = new string((name ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim().Trim('\'');
        if (clean.Length == 0) clean = "Report";
        return clean.Length > 31 ? clean.Substring(0, 31) : clean;
    }

    internal static string ColumnName(int index)
    {
        var s = "";
        while (index > 0)
        {
            int m = (index - 1) % 26;
            s = (char)('A' + m) + s;
            index = (index - m) / 26;
        }
        return s;
    }

    /// <summary>Builds the stylesheet on demand: one cell format per distinct look.</summary>
    private sealed class StyleBook
    {
        private readonly List<Font> _fonts = new() { DefaultFont() };
        private readonly List<Fill> _fills = new()
        {
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })
        };
        private readonly List<CellFormat> _formats = new() { new CellFormat() };
        private readonly Dictionary<string, uint> _map = new();

        private readonly Dictionary<string, uint> _numberFormats = new();

        /// <summary>Custom number format ids start at 164; 0 means General.</summary>
        private uint NumberFormatId(string? code)
        {
            if (code == null) return 0;
            if (!_numberFormats.TryGetValue(code, out var id))
            {
                id = 164u + (uint)_numberFormats.Count;
                _numberFormats[code] = id;
            }
            return id;
        }

        public uint Get(GridCell c, string? numberFormat = null)
        {
            string key = $"{c.Bold}|{c.Italic}|{c.FontSize}|{c.ForeColor}|{c.BackColor}|{c.Align}|{numberFormat}";
            if (_map.TryGetValue(key, out var existing)) return existing;

            var font = new Font();
            if (c.Bold) font.Append(new Bold());
            if (c.Italic) font.Append(new Italic());
            font.Append(new FontSize { Val = c.FontSize > 0 ? c.FontSize : 10 });
            var fore = ColorUtil.Parse(c.ForeColor);
            font.Append(new Color { Rgb = fore != null ? Argb(fore.Value) : "FF000000" });
            font.Append(new FontName { Val = "Calibri" });
            _fonts.Add(font);

            uint fillId = 0;
            var back = ColorUtil.Parse(c.BackColor);
            if (back != null)
            {
                _fills.Add(new Fill(new PatternFill(new ForegroundColor { Rgb = Argb(back.Value) })
                    { PatternType = PatternValues.Solid }));
                fillId = (uint)_fills.Count - 1;
            }

            var fmt = new CellFormat
            {
                NumberFormatId = NumberFormatId(numberFormat), ApplyNumberFormat = numberFormat != null,
                FontId = (uint)_fonts.Count - 1, FillId = fillId, BorderId = 0,
                ApplyFont = true, ApplyFill = fillId != 0, ApplyAlignment = true,
                Alignment = new Alignment
                {
                    Horizontal = c.Align switch
                    {
                        HAlign.Center => HorizontalAlignmentValues.Center,
                        HAlign.Right => HorizontalAlignmentValues.Right,
                        _ => HorizontalAlignmentValues.Left
                    },
                    Vertical = VerticalAlignmentValues.Center
                }
            };
            _formats.Add(fmt);
            uint id = (uint)_formats.Count - 1;
            _map[key] = id;
            return id;
        }

        public Stylesheet Build()
        {
            var sheet = new Stylesheet();
            if (_numberFormats.Count > 0)
            {
                // Must come before the fonts in the stylesheet.
                var formats = new NumberingFormats { Count = (uint)_numberFormats.Count };
                foreach (var (code, id) in _numberFormats)
                    formats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = code });
                sheet.Append(formats);
            }
            sheet.Append(new Fonts(_fonts) { Count = (uint)_fonts.Count });
            sheet.Append(new Fills(_fills) { Count = (uint)_fills.Count });
            sheet.Append(new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(),
                new BottomBorder(), new DiagonalBorder())) { Count = 1 });
            sheet.Append(new CellFormats(_formats) { Count = (uint)_formats.Count });
            return sheet;
        }

        private static Font DefaultFont() => new(new FontSize { Val = 10 }, new Color { Rgb = "FF000000" },
            new FontName { Val = "Calibri" });

        private static string Argb(System.Drawing.Color c) => $"FF{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
