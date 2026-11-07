using System.Globalization;
using ReportStudio.Core.Barcodes;
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

    /// <summary>Sub-reports the host registered by name (a sub-report item's path may be one of these names).</summary>
    public IDictionary<string, ReportDefinition>? SubReports { get; set; }

    /// <summary>Named row sets for sub-reports, linked to the parent row by a key field.</summary>
    public IDictionary<string, List<Dictionary<string, object?>>>? DataSets { get; set; }

    /// <summary>
    /// Drill-down: keys of the group instances (see RenderedItem.DrillKey) whose open/closed state differs from the group's
    /// starting state. The viewer adds a key each time the user clicks that group's header.
    /// </summary>
    public ISet<string>? ToggledGroups { get; set; }

    /// <summary>How many sub-reports deep this run is (set by the engine).</summary>
    internal int SubReportDepth { get; set; }
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
        var filterWarnings = new List<string>();
        rows = Filter(def, rows, parameters, options, filterWarnings);
        rows = Sort(def, rows, parameters, options);
        rows = OrderGroups(def, rows, parameters, options, filterWarnings);

        // Pass 1 counts the pages of every numbering section; pass 2 prints "Page n of m" with those counts.
        // Pass 1 also finds where every outline place lands, which a table of contents prints in pass 2.
        var first = new Layouter(def, rows, parameters, options, sectionTotals: null, outline: null).Run();
        first.AssignOutlineBookmarks();
        var totals = first.Pages.GroupBy(p => p.Section).ToDictionary(g => g.Key, g => g.Count());
        var result = new Layouter(def, rows, parameters, options, totals, first.FindOutline()).Run();
        result.AssignOutlineBookmarks();
        result.Warnings.InsertRange(0, filterWarnings);
        return result;
    }

    /// <summary>
    /// Keeps the rows for which the report's filter is true. A row the filter cannot be worked out for is left out
    /// and reported once, so a typo shows up as a warning and an obviously short report rather than as wrong totals.
    /// </summary>
    private static List<Row> Filter(ReportDefinition def, List<Row> rows, Row parameters, ReportOptions options,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(def.Filter)) return rows;

        var kept = new List<Row>(rows.Count);
        foreach (var row in rows)
        {
            var ctx = new EvalContext { Row = row, Parameters = parameters, Culture = options.Culture };
            try { if (Expr.IsTrue(Expr.Evaluate(def.Filter, ctx))) kept.Add(row); }
            catch (ExpressionException ex)
            {
                var message = $"Filter '{def.Filter}': {ex.Message}";
                if (!warnings.Contains(message)) warnings.Add(message);
            }
        }
        return kept;
    }

    /// <summary>Runs a report on rows supplied by the caller (the Host data source). Keys are field names.</summary>
    public static ReportResult Run(ReportDefinition def, IDictionary<string, string?>? parameterValues,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, ReportOptions? options = null)
    {
        var o = options ?? new ReportOptions();
        var copy = new ReportOptions
        {
            Culture = o.Culture, BaseDirectory = o.BaseDirectory, Measurer = o.Measurer, Now = o.Now,
            SubReports = o.SubReports, DataSets = o.DataSets, SubReportDepth = o.SubReportDepth,
            ToggledGroups = o.ToggledGroups,
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
            result[p.Name] = p.Parse(text);
        }
        return result;
    }

    private static Row ToRow(IReadOnlyDictionary<string, object?> source)
    {
        var row = new Row(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source) row[key] = Expr.Normalize(value);
        return row;
    }

    /// <summary>
    /// Puts the instances of each group in the order of its SortExpression and keeps the first TopN. Done on the rows
    /// before layout (the rows come back already in final order, still grouped), so every total, including the report
    /// footer's, counts only the groups that are kept.
    /// </summary>
    private static List<Row> OrderGroups(ReportDefinition def, List<Row> rows, Row parameters, ReportOptions options,
        List<string> warnings)
    {
        if (!def.Groups.Any(g => !string.IsNullOrWhiteSpace(g.SortExpression) || g.TopN > 0)) return rows;
        return OrderLevel(def, 0, rows, parameters, options, warnings);
    }

    private static List<Row> OrderLevel(ReportDefinition def, int level, List<Row> rows, Row parameters,
        ReportOptions options, List<string> warnings)
    {
        if (level == def.Groups.Count) return rows;
        var group = def.Groups[level];

        // The same consecutive-key split the layout uses.
        var partitions = new List<List<Row>>();
        int i = 0;
        while (i < rows.Count)
        {
            var key = SafeKey(group.Expression, new EvalContext { Row = rows[i], Parameters = parameters, Culture = options.Culture });
            int j = i + 1;
            while (j < rows.Count && Expr.Compare(key,
                       SafeKey(group.Expression, new EvalContext { Row = rows[j], Parameters = parameters, Culture = options.Culture })) == 0) j++;
            partitions.Add(rows.GetRange(i, j - i));
            i = j;
        }

        if (!string.IsNullOrWhiteSpace(group.SortExpression))
        {
            var cmp = Comparer<object?>.Create(Expr.Compare);
            var keyed = partitions.Select(p => (Rows: p, Key: GroupSortKey(group.SortExpression, p, parameters, options, warnings))).ToList();
            partitions = (group.SortDescending ? keyed.OrderByDescending(x => x.Key, cmp) : keyed.OrderBy(x => x.Key, cmp))
                .Select(x => x.Rows).ToList();     // OrderBy is stable, so ties keep the key order
        }
        if (group.TopN > 0 && partitions.Count > group.TopN) partitions = partitions.Take(group.TopN).ToList();

        var result = new List<Row>();
        foreach (var partition in partitions)
            result.AddRange(OrderLevel(def, level + 1, partition, parameters, options, warnings));
        return result;
    }

    private static object? GroupSortKey(string expression, List<Row> partition, Row parameters, ReportOptions options,
        List<string> warnings)
    {
        var ctx = new EvalContext { Row = partition[0], Scope = partition, Parameters = parameters, Culture = options.Culture };
        try { return Expr.Evaluate(expression, ctx); }
        catch (ExpressionException ex)
        {
            var message = $"Group sort '{expression}': {ex.Message}";
            if (!warnings.Contains(message)) warnings.Add(message);
            return null;
        }
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
        private readonly IReadOnlyList<OutlineEntry>? _outline;   // from pass 1; null during pass 1
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

        // Report variables (running totals): the values bands can read, and the state behind each one.
        private readonly Row _vars = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<VarState> _varStates = new();

        // Groups whose header has printed and whose footer has not yet: they are what a new page continues.
        private readonly List<(GroupDefinition Group, Row First, List<Row> Rows)> _active = new();
        private bool _continued;       // true while a header is reprinted on a continuation page
        private string _groupPath = "";   // identifies the group instance being printed (outer keys included), for drill-down
        private string _drillKey = "";    // set while a collapsible group's header prints: its items become clickable

        private sealed class VarState
        {
            public VariableDefinition Def { get; }
            public object? Value { get; set; }
            public decimal Sum { get; set; }
            public int Count { get; set; }
            public VarState(VariableDefinition def) { Def = def; }
        }

        public Layouter(ReportDefinition def, List<Row> rows, Row parameters, ReportOptions opt,
            IReadOnlyDictionary<int, int>? sectionTotals, IReadOnlyList<OutlineEntry>? outline)
        {
            _def = def; _rows = rows; _parameters = parameters; _opt = opt; _sectionTotals = sectionTotals; _outline = outline;
            _now = opt.Now ?? DateTime.Now;
            _currentRow = rows.Count > 0 ? rows[0] : _emptyRow;
            _footerRow = _currentRow;
            foreach (var v in def.Variables) _varStates.Add(new VarState(v));
            ResetVariables(s => true);
        }

        // ---- variables ------------------------------------------------------------------------

        /// <summary>Puts the matching variables back to their starting value.</summary>
        private void ResetVariables(Func<VariableDefinition, bool> which)
        {
            foreach (var s in _varStates)
            {
                if (!which(s.Def)) continue;
                s.Sum = 0; s.Count = 0;
                object? start = s.Def.Function is VariableFunction.Min or VariableFunction.Max or VariableFunction.Average ? null : 0m;
                if (!string.IsNullOrWhiteSpace(s.Def.Initial))
                {
                    try { start = Expr.Evaluate(s.Def.Initial, Ctx(_currentRow, _rows)); }
                    catch (ExpressionException ex) { Warn($"Variable '{s.Def.Name}' initial value: {ex.Message}"); }
                }
                s.Value = start;
                _vars[s.Def.Name] = start;
            }
        }

        /// <summary>Adds one printed detail row to every variable.</summary>
        private void Accumulate(Row row, List<Row> scope)
        {
            if (_varStates.Count == 0) return;
            foreach (var s in _varStates)
            {
                var def = s.Def;
                try
                {
                    var ctx = Ctx(row, scope);
                    if (!string.IsNullOrWhiteSpace(def.Condition) && !Expr.IsTrue(Expr.Evaluate(def.Condition, ctx))) continue;
                    object? value = string.IsNullOrWhiteSpace(def.Expression) ? null : Expr.Evaluate(def.Expression, ctx);

                    switch (def.Function)
                    {
                        case VariableFunction.Sum:
                            s.Value = Expr.ToNumber(s.Value) + Expr.ToNumber(value);
                            break;
                        case VariableFunction.Count:
                            if (string.IsNullOrWhiteSpace(def.Expression) || value != null) s.Value = Expr.ToNumber(s.Value) + 1;
                            break;
                        case VariableFunction.Min:
                            if (value != null && (s.Value == null || Expr.Compare(value, s.Value) < 0)) s.Value = value;
                            break;
                        case VariableFunction.Max:
                            if (value != null && (s.Value == null || Expr.Compare(value, s.Value) > 0)) s.Value = value;
                            break;
                        case VariableFunction.Average:
                            if (value != null && Expr.TryNumber(value, out var number))
                            {
                                s.Sum += number; s.Count++;
                                s.Value = s.Sum / s.Count;
                            }
                            break;
                        case VariableFunction.Assign:
                            if (!string.IsNullOrWhiteSpace(def.Expression)) s.Value = value;
                            break;
                    }
                    _vars[def.Name] = s.Value;
                }
                catch (ExpressionException ex) { Warn($"Variable '{def.Name}': {ex.Message}"); }
            }
        }

        private (object? Value, decimal Sum, int Count)[] SnapshotVariables() =>
            _varStates.Select(s => (s.Value, s.Sum, s.Count)).ToArray();

        private void RestoreVariables((object? Value, decimal Sum, int Count)[] snapshot)
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                _varStates[i].Value = snapshot[i].Value;
                _varStates[i].Sum = snapshot[i].Sum;
                _varStates[i].Count = snapshot[i].Count;
                _vars[_varStates[i].Def.Name] = snapshot[i].Value;
            }
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
                // Widow control: the last N rows travel to the next page together with the group footer.
                int keepFooter = level > 0 ? _def.Groups[level - 1].KeepFooterWithRows : 0;
                for (int k = 0; k < rows.Count; k++)
                {
                    var row = rows[k];
                    if (keepFooter > 0 && rows.Count > keepFooter && k == rows.Count - keepFooter)
                    {
                        double need = BandHeight(_def.Groups[level - 1].Footer, rows[rows.Count - 1], rows);
                        for (int t = k; t < rows.Count; t++) need += BandHeight(_def.Detail, rows[t], rows);
                        KeepOnPage(need);
                    }
                    _currentRow = row;
                    _rowNumber++;
                    RenderBand(_def.Detail, row, rows, accumulate: true);
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

                // Keep-together and orphan control decide before anything of the group prints (and before its
                // variables reset, so the page that ends here still shows the previous group's values).
                if (group.KeepTogether) KeepOnPage(MeasureGroup(level, partition));
                else if (group.KeepHeaderWithRows > 0) KeepOnPage(LeadHeight(level, partition, group.KeepHeaderWithRows));

                _currentRow = partition[0];
                int groupLevel = level + 1;
                ResetVariables(v => v.Reset == VariableReset.Group && v.ResetGroup == groupLevel);

                // Drill-down: a closed instance prints its header and footer only. Its id includes the outer groups' keys.
                var outerPath = _groupPath;
                var id = outerPath + "/" + level + ":" + Expr.Format(key, null, CultureInfo.InvariantCulture);
                bool collapsed = group.Collapsible && (group.StartCollapsed ^ (_opt.ToggledGroups?.Contains(id) == true));
                var mark = !group.Collapsible ? "" : collapsed ? "+" : "-";

                _vars["DrillMark"] = mark;
                _drillKey = group.Collapsible ? id : "";
                RenderBand(group.Header, partition[0], partition);
                _drillKey = "";
                _active.Add((group, partition[0], partition));
                _groupPath = id;
                if (!collapsed) RenderLevel(level + 1, partition);
                _groupPath = outerPath;
                _currentRow = partition[partition.Count - 1];
                _vars["DrillMark"] = mark;
                RenderBand(group.Footer, partition[partition.Count - 1], partition);
                _active.RemoveAt(_active.Count - 1);
                i = j;
            }
        }

        // ---- keeping things together ----------------------------------------------------------

        /// <summary>The body height of an empty page: the room a block has to fit into to be worth moving.</summary>
        private double PageCapacity => BodyBottom - (_def.Page.MarginTop + _def.PageHeader.Height);

        /// <summary>Starts a new page when a block of this height does not fit here but would fit on a page of its own.</summary>
        private void KeepOnPage(double need)
        {
            if (_pageHasBody && _y + need > BodyBottom && need <= PageCapacity) NewPage();
        }

        private double BandHeight(Band band, Row row, List<Row> scope)
        {
            Evaluate(band, row, scope, out double height);
            return Math.Max(0, height);
        }

        private List<List<Row>> Partitions(GroupDefinition group, List<Row> rows)
        {
            var result = new List<List<Row>>();
            int i = 0;
            while (i < rows.Count)
            {
                var key = EvalKey(group.Expression, rows[i]);
                int j = i + 1;
                while (j < rows.Count && Expr.Compare(key, EvalKey(group.Expression, rows[j])) == 0) j++;
                result.Add(rows.GetRange(i, j - i));
                i = j;
            }
            return result;
        }

        /// <summary>The height of one group instance: its header, everything inside it and its footer.</summary>
        private double MeasureGroup(int level, List<Row> partition)
        {
            var group = _def.Groups[level];
            double height = BandHeight(group.Header, partition[0], partition)
                          + BandHeight(group.Footer, partition[partition.Count - 1], partition);
            if (level + 1 == _def.Groups.Count)
            {
                foreach (var row in partition)
                {
                    height += BandHeight(_def.Detail, row, partition);
                    if (height > PageCapacity) break;      // too tall to keep on one page, so the exact figure does not matter
                }
            }
            else
            {
                foreach (var inner in Partitions(_def.Groups[level + 1], partition))
                {
                    height += MeasureGroup(level + 1, inner);
                    if (height > PageCapacity) break;
                }
            }
            return height;
        }

        /// <summary>The header, the headers of the inner groups that start with it, and the first n detail rows.</summary>
        private double LeadHeight(int level, List<Row> partition, int rowsToKeep)
        {
            double height = BandHeight(_def.Groups[level].Header, partition[0], partition);
            var here = partition;
            for (int l = level + 1; l < _def.Groups.Count; l++)
            {
                var first = Partitions(_def.Groups[l], here)[0];
                height += BandHeight(_def.Groups[l].Header, first[0], first);
                here = first;
            }
            foreach (var row in here.Take(rowsToKeep)) height += BandHeight(_def.Detail, row, here);
            return height;
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
            ResetVariables(v => v.Reset == VariableReset.Page);

            var headerRow = _rows.Count > 0 ? _rows[0] : _emptyRow;
            if (first) PlaceBand(_def.ReportHeader, headerRow, _rows);
            PlaceBand(_def.PageHeader, headerRow, _rows);

            // A group that carries on from the previous page prints its header again ("continued").
            foreach (var open in _active.Where(a => a.Group.RepeatHeader).ToList())
            {
                _continued = true;
                try { PlaceBand(open.Group.Header, open.First, open.Rows); }
                finally { _continued = false; }
            }
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
        private void RenderBand(Band band, Row row, List<Row> scope, bool accumulate = false)
        {
            // A detail row counts toward the variables before it prints, so {RunningTotal} includes its own line.
            var before = accumulate ? SnapshotVariables() : null;
            if (accumulate) Accumulate(row, scope);

            var items = Evaluate(band, row, scope, out double height);
            if (items.Count == 0 && height <= 0) return;

            if (_y + height > BodyBottom && _pageHasBody)
            {
                // The row belongs to the next page, so the finished page's footer must not include it yet.
                if (before != null) RestoreVariables(before);
                NewPage();
                if (accumulate) Accumulate(row, scope);
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
                if (_drillKey.Length > 0 && band.Kind == BandKind.GroupHeader) it.DrillKey = _drillKey;
                _page.Items.Add(it);
            }
        }

        private List<RenderedItem> Evaluate(Band band, Row row, List<Row> scope, out double height)
        {
            var ctx = Ctx(row, scope);
            var result = new List<RenderedItem>();
            height = band.Height;
            // Space handed back by shrunk elements: where it sits (across, and from where to where down the band).
            var gaps = new List<(double Left, double Right, double Start, double End)>();

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
                    BorderColor = item.BorderColor, BorderWidth = item.BorderWidth,
                    CornerRadius = item.CornerRadius
                };
                ApplyRules(item, r, ctx);
                ApplyInteraction(item, r, ctx);

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
                else if (item.Kind == ItemKind.Barcode)
                {
                    PlaceBarcode(item, r, ctx, result);
                }
                else if (item.Kind == ItemKind.QrCode)
                {
                    PlaceQr(item, r, ctx);
                }
                else if (item.Kind == ItemKind.Barcode2D)
                {
                    PlaceSymbol(item, r, ctx);
                }
                else if (item.Kind == ItemKind.CrossTab)
                {
                    double used = PlaceCrossTab(item, r, ctx, scope, result);
                    height = Math.Max(height, item.Y + Math.Max(item.Height, used));
                    continue;
                }
                else if (item.Kind == ItemKind.Contents)
                {
                    // Always as tall as designed, in both passes, so printing it never moves the pages it lists.
                    PlaceContents(item, r, result);
                    height = Math.Max(height, item.Y + item.Height);
                    continue;
                }
                else if (item.Kind == ItemKind.SubReport)
                {
                    // The child's own items are added in place of the box; the band grows to fit them.
                    double used = PlaceSubReport(item, r, ctx, row, result);
                    height = Math.Max(height, item.Y + Math.Max(item.Height, used));
                    continue;
                }

                // Suppress if blank: nothing prints. With can shrink the freed height is handed to the elements below.
                if ((item.SuppressIfBlank || item.CanShrink) && IsBlank(r))
                {
                    if (item.CanShrink) gaps.Add((r.X, r.X + item.Width, item.Y, item.Y + item.Height));
                    continue;
                }

                // a barcode's box keeps its designed height even though its bars are shorter than the caption leaves room for
                height = Math.Max(height, item.Y + (item.Kind == ItemKind.Barcode ? item.Height : r.Height));
                result.Add(r);
            }

            if (gaps.Count > 0) ShiftUp(result, gaps);
            if (result.Count == 0 && band.SuppressIfBlank) height = 0;
            else if (band.CanShrink) height = result.Count == 0 ? 0 : Math.Max(0, result.Max(x => x.Y + x.Height));
            return result;
        }

        /// <summary>Works out the item's hyperlink and bookmark for this row; an address that is not allowed is a warning and no link.</summary>
        private void ApplyInteraction(ReportItem item, RenderedItem r, EvalContext ctx)
        {
            if (!string.IsNullOrWhiteSpace(item.Link))
            {
                var raw = SafeText(item.Link, "", ctx);
                r.Link = LinkTarget.Clean(raw);
                if (r.Link.Length == 0 && !string.IsNullOrWhiteSpace(raw))
                    Warn($"Link '{raw.Trim()}' is not allowed (only http, https, mailto, tel and #bookmark) and was left out.");
            }
            if (!string.IsNullOrWhiteSpace(item.Bookmark))
                r.Bookmark = SafeText(item.Bookmark, "", ctx).Trim();
            if (!string.IsNullOrWhiteSpace(item.DrillReport))
            {
                r.DrillReport = SafeText(item.DrillReport, "", ctx).Trim();
                foreach (var part in (item.DrillParameters ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) r.DrillParameters[part.Substring(0, eq).Trim()] = SafeText(part.Substring(eq + 1).Trim(), "", ctx);
                }
            }
            if (!string.IsNullOrWhiteSpace(item.Outline) && item.Kind != ItemKind.Contents)
            {
                r.OutlineTitle = SafeText(item.Outline, "", ctx).Trim();
                r.OutlineLevel = Math.Max(1, item.OutlineLevel);
            }
        }

        /// <summary>
        /// Prints the table of contents: one line per outline place found in pass 1 (title, page number, a link to it).
        /// Lines beyond the box's height are left out with a warning.
        /// </summary>
        private void PlaceContents(ReportItem item, RenderedItem box, List<RenderedItem> output)
        {
            if (_outline == null) return;   // pass 1: the places are not known yet
            double rowHeight = Math.Max(10, item.ContentsRowHeight);
            int fit = (int)Math.Floor(item.Height / rowHeight);
            var entries = _outline.Where(e => e.Level <= Math.Max(1, item.ContentsLevels)).ToList();
            if (entries.Count > fit)
                Warn($"Table of contents '{(string.IsNullOrWhiteSpace(item.Name) ? "contents" : item.Name)}': {entries.Count - fit} line(s) do not fit; make the box taller.");

            const double numberWidth = 40;
            for (int i = 0; i < Math.Min(fit, entries.Count); i++)
            {
                var e = entries[i];
                double indent = (e.Level - 1) * item.ContentsIndent;
                double y = item.Y + i * rowHeight;
                RenderedItem Line(double x, double w, string text, HAlign align) => new()
                {
                    Kind = ItemKind.Text, X = box.X + x, Y = y, Width = Math.Max(1, w), Height = rowHeight, Text = text,
                    FontFamily = item.FontFamily, FontSize = item.FontSize, Bold = item.Bold && e.Level == 1, Italic = item.Italic,
                    Underline = item.Underline, HAlign = align, VAlign = VAlign.Middle, WordWrap = false, ForeColor = item.ForeColor,
                    Link = e.Bookmark.Length > 0 ? "#" + e.Bookmark : ""
                };
                output.Add(Line(indent, item.Width - indent - numberWidth, e.Title, HAlign.Left));
                output.Add(Line(item.Width - numberWidth, numberWidth, e.PageLabel.ToString(_opt.Culture), HAlign.Right));
            }
        }

        /// <summary>True when an element would print nothing: no text, or no picture.</summary>
        private static bool IsBlank(RenderedItem r) => r.Kind switch
        {
            ItemKind.Text or ItemKind.Barcode or ItemKind.QrCode or ItemKind.Barcode2D => string.IsNullOrWhiteSpace(r.Text),
            ItemKind.Image => r.Image == null || r.Image.Length == 0,
            _ => false
        };

        /// <summary>
        /// Moves each element up by the freed height above it that overlaps it sideways. Freed spans that share the same
        /// rows (a blank element on the left and another on the right) count once, spans one above the other add up.
        /// </summary>
        private static void ShiftUp(List<RenderedItem> items, List<(double Left, double Right, double Start, double End)> gaps)
        {
            foreach (var item in items)
            {
                var spans = gaps
                    .Where(g => g.End <= item.Y + 0.01 && item.X < g.Right && item.X + item.Width > g.Left)
                    .OrderBy(g => g.Start).ToList();

                double shift = 0, reach = double.MinValue;
                foreach (var g in spans)
                {
                    double from = Math.Max(g.Start, reach);
                    if (g.End > from) shift += g.End - from;
                    reach = Math.Max(reach, g.End);
                }
                item.Y -= shift;
            }
        }

        /// <summary>
        /// Builds a cross-tab from the rows in scope as a grid of Text items (so every export carries it):
        /// a header row, one row per distinct row value, and optionally a Total column and Total row.
        /// Returns the height used.
        /// </summary>
        private double PlaceCrossTab(ReportItem item, RenderedItem box, EvalContext ctx, List<Row> scope, List<RenderedItem> output)
        {
            var label = string.IsNullOrWhiteSpace(item.Name) ? "cross-tab" : item.Name;
            if (string.IsNullOrWhiteSpace(item.CrossRows) || string.IsNullOrWhiteSpace(item.CrossColumns) ||
                (item.CrossAggregate != CrossAggregate.Count && string.IsNullOrWhiteSpace(item.CrossValue)))
            {
                Warn($"Cross-tab '{label}': rows, columns and value must all be set.");
                return 0;
            }

            // one entry per source row: (row key, column key, number)
            var cells = new List<(object? R, object? C, decimal V)>();
            try
            {
                foreach (var source in scope)
                {
                    var rc = Ctx(source, scope);
                    var rk = Expr.Evaluate(item.CrossRows, rc);
                    var ck = Expr.Evaluate(item.CrossColumns, rc);
                    decimal v = 1;
                    if (item.CrossAggregate != CrossAggregate.Count)
                    {
                        var raw = Expr.Evaluate(item.CrossValue, rc);
                        if (raw == null) continue;                    // nothing to add for this row
                        if (raw is not decimal d)
                        {
                            Warn($"Cross-tab '{label}': the value '{item.CrossValue}' is not a number.");
                            return 0;
                        }
                        v = d;
                    }
                    cells.Add((rk, ck, v));
                }
            }
            catch (ExpressionException ex)
            {
                Warn($"Cross-tab '{label}': {ex.Message}");
                return 0;
            }

            var cmp = Comparer<object?>.Create(Expr.Compare);
            var rowKeys = DistinctSorted(cells.Select(c => c.R), cmp);
            var colKeys = DistinctSorted(cells.Select(c => c.C), cmp);
            if (rowKeys.Count == 0 || colKeys.Count == 0) return 0;

            bool totals = item.CrossTotals;
            int columnCount = colKeys.Count + (totals ? 1 : 0);
            double labelWidth = Math.Min(item.CrossLabelWidth, item.Width / 2);
            double colWidth = Math.Max(20, (item.Width - labelWidth) / columnCount);
            double rowHeight = Math.Max(10, item.CrossRowHeight);
            string numberFormat = string.IsNullOrEmpty(item.Format)
                ? (item.CrossAggregate == CrossAggregate.Count ? "N0" : "N2") : item.Format;

            string Summary(IEnumerable<decimal> values)
            {
                var list = values.ToList();
                if (list.Count == 0) return "";
                decimal result = item.CrossAggregate switch
                {
                    CrossAggregate.Count => list.Count,
                    CrossAggregate.Average => list.Average(),
                    CrossAggregate.Min => list.Min(),
                    CrossAggregate.Max => list.Max(),
                    _ => list.Sum()
                };
                return result.ToString(numberFormat, _opt.Culture);
            }

            RenderedItem Cell(double x, double y, double w, string text, bool header, bool right)
            {
                var ri = new RenderedItem
                {
                    Kind = ItemKind.Text, X = box.X + x, Y = item.Y + y, Width = w, Height = rowHeight, Text = text,
                    FontFamily = item.FontFamily, FontSize = item.FontSize, Bold = header || item.Bold, Italic = item.Italic,
                    HAlign = right ? HAlign.Right : HAlign.Left, VAlign = VAlign.Middle, WordWrap = false,
                    ForeColor = item.ForeColor,
                    BackColor = header ? (string.IsNullOrEmpty(item.BackColor) ? "#E5E7EB" : item.BackColor) : "",
                    BorderColor = string.IsNullOrEmpty(item.BorderColor) ? "#9CA3AF" : item.BorderColor,
                    BorderWidth = item.BorderWidth > 0 ? item.BorderWidth : 1
                };
                output.Add(ri);
                return ri;
            }

            string Key(object? k) => Expr.Format(k, null, _opt.Culture);

            // header row
            Cell(0, 0, labelWidth, item.CrossCorner, header: true, right: false);
            for (int c = 0; c < colKeys.Count; c++)
                Cell(labelWidth + c * colWidth, 0, colWidth, Key(colKeys[c]), header: true, right: true);
            if (totals) Cell(labelWidth + colKeys.Count * colWidth, 0, colWidth, "Total", header: true, right: true);

            // body rows
            for (int r = 0; r < rowKeys.Count; r++)
            {
                double y = (r + 1) * rowHeight;
                var inRow = cells.Where(x => Expr.Compare(x.R, rowKeys[r]) == 0).ToList();
                Cell(0, y, labelWidth, Key(rowKeys[r]), header: true, right: false);
                for (int c = 0; c < colKeys.Count; c++)
                    Cell(labelWidth + c * colWidth, y, colWidth,
                        Summary(inRow.Where(x => Expr.Compare(x.C, colKeys[c]) == 0).Select(x => x.V)), header: false, right: true);
                if (totals)
                    Cell(labelWidth + colKeys.Count * colWidth, y, colWidth, Summary(inRow.Select(x => x.V)), header: true, right: true);
            }

            // total row
            double rows = rowKeys.Count + 1;
            if (totals)
            {
                double y = rows * rowHeight;
                Cell(0, y, labelWidth, "Total", header: true, right: false);
                for (int c = 0; c < colKeys.Count; c++)
                    Cell(labelWidth + c * colWidth, y, colWidth,
                        Summary(cells.Where(x => Expr.Compare(x.C, colKeys[c]) == 0).Select(x => x.V)), header: true, right: true);
                Cell(labelWidth + colKeys.Count * colWidth, y, colWidth, Summary(cells.Select(x => x.V)), header: true, right: true);
                rows++;
            }
            return rows * rowHeight;
        }

        private static List<object?> DistinctSorted(IEnumerable<object?> keys, IComparer<object?> comparer)
        {
            var list = new List<object?>();
            foreach (var key in keys)
                if (!list.Any(existing => comparer.Compare(existing, key) == 0)) list.Add(key);
            list.Sort(comparer);
            return list;
        }

        private const int MaxSubReportDepth = 3;

        /// <summary>
        /// Runs the sub-report on its rows and copies its items into this band, moved to the element's position.
        /// Returns the height the sub-report needs. Its page header/footer are not used; it must fit on one page.
        /// </summary>
        private double PlaceSubReport(ReportItem item, RenderedItem box, EvalContext ctx, Row row, List<RenderedItem> output)
        {
            var label = string.IsNullOrWhiteSpace(item.Name) ? item.SubReportPath : item.Name;
            if (_opt.SubReportDepth >= MaxSubReportDepth)
            {
                Warn($"Sub-report '{label}': sub-reports can be nested only {MaxSubReportDepth} deep.");
                return 0;
            }

            var child = LoadSubReport(item, label);
            if (child == null) return 0;
            var childRows = SubReportRows(item, row, label);

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (item.SubReportParameters ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                values[part.Substring(0, eq).Trim()] = SafeText(part.Substring(eq + 1).Trim(), "", ctx);
            }

            var options = new ReportOptions
            {
                Culture = _opt.Culture, BaseDirectory = _opt.BaseDirectory, Measurer = _opt.Measurer, Now = _now,
                SubReports = _opt.SubReports, DataSets = _opt.DataSets, SubReportDepth = _opt.SubReportDepth + 1,
                Rows = childRows
            };

            ReportResult res;
            try { res = ReportEngine.Run(child, values, options); }   // qualified: Layouter has its own Run()
            catch (InvalidOperationException ex)
            {
                Warn($"Sub-report '{label}': {ex.Message}");
                return 0;
            }

            foreach (var w in res.Warnings) Warn($"Sub-report '{label}': {w}");
            if (res.Pages.Count == 0) return 0;
            if (res.Pages.Count > 1)
                Warn($"Sub-report '{label}' is longer than one page; only its first page is shown.");

            double used = 0;
            foreach (var it in res.Pages[0].Items)
            {
                it.X = box.X + (it.X - child.Page.MarginLeft);
                it.Y = item.Y + (it.Y - child.Page.MarginTop);
                used = Math.Max(used, it.Y + it.Height - item.Y);
                output.Add(it);
            }
            return used;
        }

        /// <summary>Finds the sub-report: a host-registered name first, else a .rsr inside the report's own folder.</summary>
        private ReportDefinition? LoadSubReport(ReportItem item, string label)
        {
            var name = (item.SubReportPath ?? "").Trim();
            if (name.Length == 0)
            {
                Warn($"Sub-report '{label}': no report chosen.");
                return null;
            }

            try
            {
                ReportDefinition def;
                if (_opt.SubReports != null && _opt.SubReports.TryGetValue(name, out var registered))
                    def = registered.Clone();
                else
                {
                    if (_opt.BaseDirectory == null)
                    {
                        Warn($"Sub-report '{label}': '{name}' is not registered and there is no folder to look in.");
                        return null;
                    }
                    var root = Path.GetFullPath(_opt.BaseDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var full = Path.GetFullPath(Path.Combine(root, name));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        !full.EndsWith(".rsr", StringComparison.OrdinalIgnoreCase))
                    {
                        Warn($"Sub-report '{label}': only .rsr files inside the report's folder are allowed.");
                        return null;
                    }
                    if (!File.Exists(full))
                    {
                        Warn($"Sub-report '{label}': file not found: {name}");
                        return null;
                    }
                    def = ReportDefinition.Load(full);
                }

                // Always a host report (its own query is never run); only the body is used.
                def.DataSource.Provider = DbProvider.Host;
                def.PageHeader.Items.Clear(); def.PageHeader.Height = 0;
                def.PageFooter.Items.Clear(); def.PageFooter.Height = 0;
                return def;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Warn($"Sub-report '{label}': {ex.Message}");
                return null;
            }
        }

        /// <summary>The sub-report's rows: a list held in the parent row, else a host data set filtered by the link fields.</summary>
        private List<Row> SubReportRows(ReportItem item, Row row, string label)
        {
            var rows = new List<Row>();
            var source = (item.SubReportData ?? "").Trim();
            if (source.Length == 0) return rows;

            if (row.TryGetValue(source, out var nested) && nested is System.Collections.IEnumerable list && nested is not string)
            {
                foreach (var entry in list) AddRow(rows, entry);
                return rows;
            }

            if (_opt.DataSets == null || !_opt.DataSets.TryGetValue(source, out var set))
            {
                Warn($"Sub-report '{label}': no data called '{source}'.");
                return rows;
            }

            var childKey = (item.LinkChild ?? "").Trim();
            var parentKey = string.IsNullOrWhiteSpace(item.LinkParent) ? childKey : item.LinkParent.Trim();
            row.TryGetValue(parentKey, out var parentValue);
            parentValue = Expr.Normalize(parentValue);

            foreach (var candidate in set)
            {
                var copy = ToRow(candidate);
                if (childKey.Length > 0)
                {
                    copy.TryGetValue(childKey, out var childValue);
                    if (Expr.Compare(childValue, parentValue) != 0) continue;
                }
                rows.Add(copy);
            }
            return rows;
        }

        private static void AddRow(List<Row> rows, object? entry)
        {
            switch (entry)
            {
                case IReadOnlyDictionary<string, object?> ro: rows.Add(ToRow(ro)); break;
                case IDictionary<string, object?> rw: rows.Add(ToRow(new Dictionary<string, object?>(rw))); break;
            }
        }

        /// <summary>Fills in a QR code. A value that cannot be encoded (empty, or too long) prints nothing and warns.</summary>
        private void PlaceQr(ReportItem item, RenderedItem qr, EvalContext ctx)
        {
            qr.QrLevel = item.QrLevel;

            string value;
            try { value = TextTemplate.Parse(item.Text).EvaluateString(ctx, item.Format); }
            catch (ExpressionException ex)
            {
                Warn($"QR code '{item.Name}': {ex.Message}");
                return;
            }

            if (!QrEncoder.TryEncode(value, item.QrLevel, out _, out var reason))
            {
                Warn($"QR code '{item.Name}': {reason}");
                return;
            }
            qr.Text = value;
        }

        private void PlaceSymbol(ReportItem item, RenderedItem symbol, EvalContext ctx)
        {
            symbol.Barcode2DType = item.Barcode2DType;

            string value;
            try { value = TextTemplate.Parse(item.Text).EvaluateString(ctx, item.Format); }
            catch (ExpressionException ex)
            {
                Warn($"2D code '{item.Name}': {ex.Message}");
                return;
            }

            if (!Symbol2D.TryEncode(item.Barcode2DType, value, out _, out _, out var reason))
            {
                Warn($"2D code '{item.Name}': {reason}");
                return;
            }
            symbol.Text = value;
        }

        /// <summary>
        /// Fills in a barcode: the bars item gets the value, and the readable text becomes a separate centred
        /// text item under the bars (so Excel and Word exports carry it too). An unreadable value prints nothing and warns.
        /// </summary>
        private void PlaceBarcode(ReportItem item, RenderedItem bars, EvalContext ctx, List<RenderedItem> output)
        {
            bars.BarcodeType = item.BarcodeType;

            string value;
            try { value = TextTemplate.Parse(item.Text).EvaluateString(ctx, item.Format); }
            catch (ExpressionException ex)
            {
                Warn($"Barcode '{item.Name}': {ex.Message}");
                return;
            }

            if (!BarcodeEncoder.TryEncode(item.BarcodeType, value, out _, out var reason))
            {
                Warn($"Barcode '{item.Name}': {reason}");
                return;
            }

            bars.Text = value;
            if (!item.ShowText) return;

            double captionHeight = Math.Min(item.Height / 2, Math.Ceiling(item.FontSize * 96.0 / 72.0 * 1.3));
            bars.Height = item.Height - captionHeight;
            output.Add(new RenderedItem
            {
                Kind = ItemKind.Text,
                X = bars.X, Y = item.Y + bars.Height, Width = item.Width, Height = captionHeight,
                Text = BarcodeEncoder.PrintedText(item.BarcodeType, value),   // EAN/UPC with the check digit that was added
                FontFamily = item.FontFamily, FontSize = item.FontSize, Bold = item.Bold, Italic = item.Italic,
                HAlign = HAlign.Center, VAlign = VAlign.Middle, WordWrap = false,
                ForeColor = item.ForeColor
            });
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

        /// <summary>Conditional formatting: every rule that is true changes the printed copy, in order (the design is untouched).</summary>
        private void ApplyRules(ReportItem item, RenderedItem r, EvalContext ctx)
        {
            foreach (var rule in item.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Condition)) continue;
                try { if (!Expr.IsTrue(Expr.Evaluate(rule.Condition, ctx))) continue; }
                catch (ExpressionException ex) { Warn($"Format rule '{rule.Condition}': {ex.Message}"); continue; }

                if (!string.IsNullOrWhiteSpace(rule.ForeColor)) r.ForeColor = rule.ForeColor;
                if (!string.IsNullOrWhiteSpace(rule.BackColor)) r.BackColor = rule.BackColor;
                if (!string.IsNullOrWhiteSpace(rule.BorderColor)) r.BorderColor = rule.BorderColor;
                if (rule.Bold.HasValue) r.Bold = rule.Bold.Value;
                if (rule.Italic.HasValue) r.Italic = rule.Italic.Value;
                if (rule.Underline.HasValue) r.Underline = rule.Underline.Value;
            }
        }

        private EvalContext Ctx(Row row, List<Row> scope) => new()
        {
            Row = row,
            Scope = scope,
            Parameters = _parameters,
            Variables = _vars,
            Continued = _continued,
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
