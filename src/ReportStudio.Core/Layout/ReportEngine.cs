using System.Globalization;
using ReportStudio.Core.Data;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Layout;

using Row = Dictionary<string, object?>;

public class ReportOptions
{
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;
    /// <summary>Folder used to resolve relative ImagePath values (normally the .rsr file's folder).</summary>
    public string? BaseDirectory { get; set; }
    public ITextMeasurer Measurer { get; set; } = new EstimatingTextMeasurer();
    /// <summary>Supply rows directly (tests, other data sources) instead of querying the report's data source.</summary>
    public List<Row>? Rows { get; set; }
    public DateTime? Now { get; set; }
}

/// <summary>
/// Turns a ReportDefinition + data + parameters into paginated, fully resolved pages.
/// Runs two passes so {PageCount} is known on every page.
/// </summary>
public static class ReportEngine
{
    public static ReportResult Run(ReportDefinition def, IDictionary<string, string?>? parameterValues = null,
        ReportOptions? options = null)
    {
        options ??= new ReportOptions();
        def.Normalize();

        var parameters = ResolveParameters(def, parameterValues);

        List<Row> rows;
        if (options.Rows != null) rows = options.Rows;
        else if (def.DataSource.Provider == DbProvider.Host)
            throw new InvalidOperationException(
                "This report takes its data from the host application. Call Run with the rows supplied.");
        else rows = DataLoader.Load(def.DataSource, parameters);
        rows = Sort(def, rows, parameters, options);

        // Pass 1 counts the pages of every numbering section; pass 2 prints "Page n of m" with those counts.
        var first = new Layouter(def, rows, parameters, options, sectionTotals: null).Run();
        var totals = first.Pages.GroupBy(p => p.Section).ToDictionary(g => g.Key, g => g.Count());
        return new Layouter(def, rows, parameters, options, totals).Run();
    }

    /// <summary>Runs a report on rows supplied by the caller (the Host data source). Keys are field names.</summary>
    public static ReportResult Run(ReportDefinition def, IDictionary<string, string?>? parameterValues,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, ReportOptions? options = null)
    {
        var o = options ?? new ReportOptions();
        var copy = new ReportOptions
        {
            Culture = o.Culture, BaseDirectory = o.BaseDirectory, Measurer = o.Measurer, Now = o.Now,
            Rows = rows.Select(ToRow).ToList()
        };
        return Run(def, parameterValues, copy);
    }

    /// <summary>Parses raw parameter text into typed values (decimal, DateTime, bool, string) using each parameter's type and default.</summary>
    public static Dictionary<string, object?> ResolveParameters(ReportDefinition def, IDictionary<string, string?>? raw)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in def.Parameters)
        {
            string? text = null;
            raw?.TryGetValue(p.Name, out text);
            result[p.Name] = p.Parse(text ?? p.DefaultValue);
        }
        return result;
    }

    private static Row ToRow(IReadOnlyDictionary<string, object?> source)
    {
        var row = new Row(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source) row[key] = Expr.Normalize(value);
        return row;
    }

    private static List<Row> Sort(ReportDefinition def, List<Row> rows, Row parameters, ReportOptions options)
    {
        var exprs = def.Groups.Select(g => g.Expression).Concat(def.SortBy)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (exprs.Count == 0) return rows;

        var cmp = Comparer<object?>.Create(Expr.Compare);
        var keyed = rows.Select(r =>
        {
            var ctx = new EvalContext { Row = r, Parameters = parameters, Culture = options.Culture };
            return new KeyedRow(r, exprs.Select(e => SafeKey(e, ctx)).ToArray());
        }).ToList();

        IOrderedEnumerable<KeyedRow>? ordered = null;
        for (int i = 0; i < exprs.Count; i++)
        {
            int k = i;
            ordered = ordered == null ? keyed.OrderBy(x => x.Keys[k], cmp) : ordered.ThenBy(x => x.Keys[k], cmp);
        }
        return ordered!.Select(x => x.Data).ToList();
    }

    /// <summary>A bad sort/group expression sorts as null; the layout pass reports it as a warning.</summary>
    private static object? SafeKey(string expression, EvalContext ctx)
    {
        try { return Expr.Evaluate(expression, ctx); }
        catch (ExpressionException) { return null; }
    }

    private sealed class KeyedRow
    {
        public Row Data { get; }
        public object?[] Keys { get; }
        public KeyedRow(Row data, object?[] keys) { Data = data; Keys = keys; }
    }

    // ------------------------------------------------------------------------------------------

    private sealed class Layouter
    {
        private readonly ReportDefinition _def;
        private readonly List<Row> _rows;
        private readonly Row _parameters;
        private readonly ReportOptions _opt;
        private readonly IReadOnlyDictionary<int, int>? _sectionTotals;
        private readonly DateTime _now;
        private readonly Row _emptyRow = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warned = new();
        private readonly ReportResult _result = new();

        private RenderedPage _page = null!;
        private double _y;
        private int _pageNumber;       // absolute page index in the output
        private int _section = 1;      // numbering section; a group with RestartPageNumbers starts a new one
        private int _sectionPage;      // page number inside the current section (what {Page} shows)
        private int _rowNumber;
        private int _bandInstance;
        private bool _pageHasBody;
        private Row _currentRow;
        private Row _footerRow;

        public Layouter(ReportDefinition def, List<Row> rows, Row parameters, ReportOptions opt,
            IReadOnlyDictionary<int, int>? sectionTotals)
        {
            _def = def; _rows = rows; _parameters = parameters; _opt = opt; _sectionTotals = sectionTotals;
            _now = opt.Now ?? DateTime.Now;
            _currentRow = rows.Count > 0 ? rows[0] : _emptyRow;
            _footerRow = _currentRow;
        }

        private double BodyBottom => _def.Page.Height - _def.Page.MarginBottom - _def.PageFooter.Height;

        public ReportResult Run()
        {
            _result.ReportName = _def.Name;
            _result.RowCount = _rows.Count;

            BeginPage(first: true);
            RenderLevel(0, _rows);
            RenderBand(_def.ReportFooter, _currentRow, _rows);
            EndPage();
            return _result;
        }

        // ---- grouping -------------------------------------------------------------------------

        private void RenderLevel(int level, List<Row> rows)
        {
            if (level == _def.Groups.Count)
            {
                foreach (var row in rows)
                {
                    _currentRow = row;
                    _rowNumber++;
                    RenderBand(_def.Detail, row, rows);
                }
                return;
            }

            var group = _def.Groups[level];
            int i = 0;
            while (i < rows.Count)
            {
                var key = EvalKey(group.Expression, rows[i]);
                int j = i + 1;
                while (j < rows.Count && Expr.Compare(key, EvalKey(group.Expression, rows[j])) == 0) j++;
                var partition = rows.GetRange(i, j - i);

                if ((group.StartNewPage || group.RestartPageNumbers) && _pageHasBody)
                    NewPage(restartNumbering: group.RestartPageNumbers);
                _currentRow = partition[0];
                RenderBand(group.Header, partition[0], partition);
                RenderLevel(level + 1, partition);
                _currentRow = partition[partition.Count - 1];
                RenderBand(group.Footer, partition[partition.Count - 1], partition);
                i = j;
            }
        }

        private object? EvalKey(string expression, Row row)
        {
            try { return Expr.Evaluate(expression, Ctx(row, _rows)); }
            catch (ExpressionException ex) { Warn($"Group expression '{expression}': {ex.Message}"); return null; }
        }

        // ---- pages ----------------------------------------------------------------------------

        private void BeginPage(bool first)
        {
            _sectionPage++;
            _page = new RenderedPage
            {
                Number = ++_pageNumber, Section = _section,
                Width = _def.Page.Width, Height = _def.Page.Height
            };
            _result.Pages.Add(_page);
            _y = _def.Page.MarginTop;
            _bandInstance = 0;
            _pageHasBody = false;

            var headerRow = _rows.Count > 0 ? _rows[0] : _emptyRow;
            if (first) PlaceBand(_def.ReportHeader, headerRow, _rows);
            PlaceBand(_def.PageHeader, headerRow, _rows);
        }

        private void EndPage()
        {
            var footer = _def.PageFooter;
            var saved = _y;
            _y = _def.Page.Height - _def.Page.MarginBottom - footer.Height;
            PlaceBand(footer, _footerRow, _rows);   // the last row actually printed, not the next group's first row
            _y = saved;
        }

        private void NewPage(bool restartNumbering = false)
        {
            EndPage();   // the finished page keeps its own numbering
            if (restartNumbering) { _section++; _sectionPage = 0; }
            BeginPage(first: false);
        }

        // ---- bands ----------------------------------------------------------------------------

        /// <summary>Renders a flowing body band, breaking to a new page first when it does not fit.</summary>
        private void RenderBand(Band band, Row row, List<Row> scope)
        {
            var items = Evaluate(band, row, scope, out double height);
            if (items.Count == 0 && height <= 0) return;

            if (_y + height > BodyBottom && _pageHasBody)
            {
                NewPage();
                // Evaluate again on the new page so {Page} and friends show the right values.
                items = Evaluate(band, row, scope, out height);
            }
            Emit(band, items);
            _y += height;
            _pageHasBody = true;
            _footerRow = row;
        }

        /// <summary>Renders a header/footer band at the current Y without page-break logic.</summary>
        private void PlaceBand(Band band, Row row, List<Row> scope)
        {
            var items = Evaluate(band, row, scope, out double height);
            Emit(band, items);
            _y += height;
        }

        private void Emit(Band band, List<RenderedItem> items)
        {
            _bandInstance++;
            foreach (var it in items)
            {
                it.Y += _y;
                it.Band = band.Kind;
                it.BandInstance = _bandInstance;
                _page.Items.Add(it);
            }
        }

        private List<RenderedItem> Evaluate(Band band, Row row, List<Row> scope, out double height)
        {
            var ctx = Ctx(row, scope);
            var result = new List<RenderedItem>();
            height = band.Height;

            foreach (var item in band.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.VisibleWhen))
                {
                    try { if (!Expr.IsTrue(Expr.Evaluate(item.VisibleWhen, ctx))) continue; }
                    catch (ExpressionException ex) { Warn($"VisibleWhen '{item.VisibleWhen}': {ex.Message}"); }
                }

                var r = new RenderedItem
                {
                    Kind = item.Kind,
                    X = _def.Page.MarginLeft + item.X,
                    Y = item.Y,
                    Width = item.Width,
                    Height = item.Height,
                    FontFamily = item.FontFamily, FontSize = item.FontSize,
                    Bold = item.Bold, Italic = item.Italic, Underline = item.Underline,
                    HAlign = item.HAlign, VAlign = item.VAlign, WordWrap = item.WordWrap,
                    ForeColor = item.ForeColor, BackColor = item.BackColor,
                    BorderColor = item.BorderColor, BorderWidth = item.BorderWidth
                };

                if (item.Kind == ItemKind.Text)
                {
                    r.Text = SafeText(item.Text, item.Format, ctx);
                    if (item.CanGrow && r.Text.Length > 0)
                        r.Height = Math.Max(item.Height, _opt.Measurer.MeasureHeight(r.Text, item, item.Width));
                }
                else if (item.Kind == ItemKind.Image)
                {
                    r.Image = LoadImage(item, ctx);
                }

                height = Math.Max(height, item.Y + r.Height);
                result.Add(r);
            }
            return result;
        }

        private string SafeText(string text, string format, EvalContext ctx)
        {
            try { return TextTemplate.Parse(text).EvaluateString(ctx, format); }
            catch (ExpressionException ex)
            {
                Warn($"'{text}': {ex.Message}");
                return "#ERR";
            }
        }

        private byte[]? LoadImage(ReportItem item, EvalContext ctx)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.ImageBase64)) return Convert.FromBase64String(item.ImageBase64);
                if (string.IsNullOrWhiteSpace(item.ImagePath)) return null;

                var path = TextTemplate.Parse(item.ImagePath).EvaluateString(ctx, null);
                if (!Path.IsPathRooted(path) && _opt.BaseDirectory != null) path = Path.Combine(_opt.BaseDirectory, path);
                if (File.Exists(path)) return File.ReadAllBytes(path);
                Warn($"Image not found: {path}");
            }
            catch (Exception ex) when (ex is FormatException or IOException or ExpressionException or UnauthorizedAccessException)
            {
                Warn($"Image '{item.Name}': {ex.Message}");
            }
            return null;
        }

        private EvalContext Ctx(Row row, List<Row> scope) => new()
        {
            Row = row,
            Scope = scope,
            Parameters = _parameters,
            PageNumber = _sectionPage,
            TotalPages = _sectionTotals != null && _sectionTotals.TryGetValue(_section, out var total) ? total : _sectionPage,
            RowNumber = _rowNumber,
            Now = _now,
            Culture = _opt.Culture
        };

        private void Warn(string message)
        {
            if (_warned.Add(message)) _result.Warnings.Add(message);
        }
    }
}
