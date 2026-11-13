using System.Globalization;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Works out the live Excel formula behind a total cell: {Sum(Amount)} in a group footer becomes =SUM(D5:D9,D12:D15) over the
/// detail cells of that group. A formula is only given when it reproduces the number the report printed (within the rounding of
/// the printed values); otherwise the cell stays a plain number, so Excel never shows a different total than the PDF.
/// </summary>
internal sealed class ExcelFormulas
{
    private const int ArgumentsPerCall = 200;   // Excel allows 255 arguments in one call
    private const int MaxLength = 8000;         // Excel allows 8192 characters in one formula

    private readonly Grid _grid;
    private readonly int[] _sheetOf;
    private readonly int[] _rowNumber;
    private readonly IReadOnlyList<string> _sheetNames;
    private readonly CultureInfo _culture;
    private readonly List<int> _detailRows = new();
    private readonly Dictionary<string, List<(int Row, GridCell Cell)>> _byField = new(StringComparer.OrdinalIgnoreCase);

    public ExcelFormulas(Grid grid, int[] sheetOf, int[] rowNumber, IReadOnlyList<string> sheetNames, CultureInfo culture)
    {
        _grid = grid;
        _sheetOf = sheetOf;
        _rowNumber = rowNumber;
        _sheetNames = sheetNames;
        _culture = culture;

        for (int i = 0; i < grid.Rows.Count; i++)
        {
            var row = grid.Rows[i];
            if (row.Band != BandKind.Detail) continue;
            _detailRows.Add(i);
            foreach (var cell in row.Cells)
            {
                if (cell.Field.Length == 0) continue;
                if (!_byField.TryGetValue(cell.Field, out var list)) _byField[cell.Field] = list = new();
                list.Add((i, cell));
            }
        }
    }

    /// <summary>The formula (without the leading =) for this cell, or null when it is not a total Excel can reproduce.</summary>
    public string? For(int rowIndex, GridCell cell)
    {
        if (cell.AggFunction.Length == 0) return null;
        var total = _grid.Rows[rowIndex];
        if (total.Band is not (BandKind.GroupHeader or BandKind.GroupFooter or BandKind.ReportFooter)) return null;
        if (!TryNumeric(cell.Text, _culture, out double shown, out int shownDecimals)) return null;

        bool countRows = cell.AggFunction == "Count" && cell.AggField.Length == 0;
        var cells = new List<(int Row, int Column, string Text)>();
        if (countRows)
        {
            foreach (int d in _detailRows)
            {
                var detail = _grid.Rows[d];
                if (!detail.FirstLine || detail.Cells.Count == 0 || !InScope(total, detail)) continue;
                // Several detail rows printed side by side in one line (columns across): the lines are not the rows.
                if (detail.Cells.Where(c => c.Field.Length > 0).GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    return null;
                cells.Add((d, detail.Cells[0].Column, ""));
            }
        }
        else
        {
            if (!_byField.TryGetValue(cell.AggField, out var list)) return null;
            foreach (var (d, c) in list)
                if (InScope(total, _grid.Rows[d])) cells.Add((d, c.Column, c.Text));
        }
        if (cells.Count == 0) return null;

        if (!Matches(cell.AggFunction, cells, shown, shownDecimals)) return null;

        var refs = Ranges(cells, _sheetOf[rowIndex]);
        var formula = Build(cell.AggFunction, countRows, refs);
        return formula != null && formula.Length <= MaxLength ? formula : null;
    }

    /// <summary>Does this total cover the detail row? A report footer covers all of them, a group's total those inside that group instance.</summary>
    private static bool InScope(GridRow total, GridRow detail)
    {
        if (total.Band == BandKind.ReportFooter) return true;
        int level = total.GroupLevel;
        return level >= 1 && total.GroupIds.Length >= level && detail.GroupIds.Length >= level
            && detail.GroupIds[level - 1] == total.GroupIds[level - 1];
    }

    /// <summary>True when computing the total over the printed detail cells gives the printed total, allowing for rounding.</summary>
    private bool Matches(string function, List<(int Row, int Column, string Text)> cells, double shown, int shownDecimals)
    {
        double shownRounding = 0.5 * Math.Pow(10, -shownDecimals);
        if (function == "Count") return Math.Abs(cells.Count - shown) < 0.5;

        var values = new List<double>();
        int detailDecimals = 0;
        foreach (var c in cells)
            if (TryNumeric(c.Text, _culture, out double v, out int dd))
            {
                values.Add(v);
                detailDecimals = Math.Max(detailDecimals, dd);
            }
        if (values.Count == 0) return false;

        double detailRounding = 0.5 * Math.Pow(10, -detailDecimals);
        double expected, tolerance;
        switch (function)
        {
            case "Sum": expected = values.Sum(); tolerance = shownRounding + values.Count * detailRounding; break;
            case "Avg": expected = values.Average(); tolerance = shownRounding + detailRounding; break;
            case "Min": expected = values.Min(); tolerance = shownRounding + detailRounding; break;
            case "Max": expected = values.Max(); tolerance = shownRounding + detailRounding; break;
            default: return false;
        }
        return Math.Abs(expected - shown) <= tolerance + 1e-9;
    }

    /// <summary>The cells as Excel ranges: runs of rows in one column of one sheet, written with the sheet name when it is another sheet.</summary>
    private List<string> Ranges(List<(int Row, int Column, string Text)> cells, int ownSheet)
    {
        var sorted = cells.Select(c => (Sheet: _sheetOf[c.Row], c.Column, No: _rowNumber[c.Row]))
            .Distinct().OrderBy(s => s.Sheet).ThenBy(s => s.Column).ThenBy(s => s.No).ToList();
        var refs = new List<string>();
        int i = 0;
        while (i < sorted.Count)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].Sheet == sorted[i].Sheet && sorted[j + 1].Column == sorted[i].Column
                   && sorted[j + 1].No == sorted[j].No + 1) j++;
            string col = ExcelExporter.ColumnName(sorted[i].Column + 1);
            string range = j == i ? col + sorted[i].No : col + sorted[i].No + ":" + col + sorted[j].No;
            if (sorted[i].Sheet != ownSheet) range = Quote(_sheetNames[sorted[i].Sheet]) + "!" + range;
            refs.Add(range);
            i = j + 1;
        }
        return refs;
    }

    private static string Quote(string sheetName) => "'" + sheetName.Replace("'", "''") + "'";

    private static string? Build(string function, bool countRows, List<string> refs)
    {
        switch (function)
        {
            case "Sum": return Combine("SUM", "SUM", refs);
            case "Min": return Combine("MIN", "MIN", refs);
            case "Max": return Combine("MAX", "MAX", refs);
            case "Count":
                return countRows
                    ? Combine("SUM", "SUM", refs.Select(r => "ROWS(" + r + ")").ToList())
                    : Combine("COUNTA", "SUM", refs);
            case "Avg":
                var sum = Combine("SUM", "SUM", refs);
                var count = Combine("COUNT", "SUM", refs);
                return sum == null || count == null ? null : sum + "/" + count;
            default: return null;
        }
    }

    /// <summary>inner(a,b,c); with many arguments, groups of them each go through inner and the results through outer.</summary>
    private static string? Combine(string inner, string outer, List<string> args)
    {
        if (args.Count <= ArgumentsPerCall) return inner + "(" + string.Join(",", args) + ")";
        var parts = new List<string>();
        for (int k = 0; k < args.Count; k += ArgumentsPerCall)
            parts.Add(inner + "(" + string.Join(",", args.Skip(k).Take(ArgumentsPerCall)) + ")");
        return parts.Count > ArgumentsPerCall ? null : outer + "(" + string.Join(",", parts) + ")";
    }

    /// <summary>A printed number or date as a value, with the number of decimals it was printed with.</summary>
    internal static bool TryNumeric(string text, CultureInfo culture, out double value, out int decimals)
    {
        value = 0;
        decimals = 0;
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (DateTime.TryParseExact(t, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = date.ToOADate();
            return true;
        }
        if (t.Length > 1 && t[0] == '0' && char.IsDigit(t[1])) return false;   // identifiers such as 007 stay text
        if (!decimal.TryParse(t, NumberStyles.Number, culture, out var number)) return false;
        value = (double)number;
        var nf = culture.NumberFormat;
        int point = t.IndexOf(nf.NumberDecimalSeparator, StringComparison.Ordinal);
        decimals = point < 0 ? 0 : t.Length - point - nf.NumberDecimalSeparator.Length;
        return true;
    }
}
