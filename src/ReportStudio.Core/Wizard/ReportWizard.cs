using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Wizard;

/// <summary>How the rows of the new report are laid out.</summary>
public enum WizardLayout
{
    /// <summary>One line for each record, a column for each field, column titles in the page header.</summary>
    Table,
    /// <summary>One block for each record: the field name at the left, its value next to it.</summary>
    Form
}

/// <summary>The colours and rules of the new report.</summary>
public enum WizardStyle { Corporate, Simple, Modern, Classic }

public enum WizardPaper { A4, Letter }

/// <summary>What the report wizard asks (requirement 45): the data, the fields to show, how to group and total them, and the look.</summary>
public class WizardOptions
{
    public const int MaxGroups = 3;

    public string Name { get; set; } = "New report";

    /// <summary>The heading printed at the top. Empty = the name.</summary>
    public string Title { get; set; } = "";

    /// <summary>Where the rows come from. A copy is stored in the report; when it lists no fields, <see cref="Fields"/> is used.</summary>
    public DataSourceDefinition DataSource { get; set; } = new();

    /// <summary>Every field the data has.</summary>
    public List<FieldDefinition> Fields { get; set; } = new();

    /// <summary>The fields to show, in order. Empty = all of them.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>The fields to group by, outermost first (at most three). A grouped field is printed in the group header, not as a column.</summary>
    public List<string> GroupBy { get; set; } = new();

    /// <summary>Number fields to add up in every group and at the end. Each must also be shown as a column.</summary>
    public List<string> TotalFields { get; set; } = new();

    public WizardLayout Layout { get; set; } = WizardLayout.Table;
    public WizardStyle Style { get; set; } = WizardStyle.Corporate;
    public WizardPaper Paper { get; set; } = WizardPaper.A4;
    public bool Landscape { get; set; }

    public bool PageNumbers { get; set; } = true;
    public bool PrintedDate { get; set; } = true;

    /// <summary>A total line at the end (the totals, or the number of records when no field is added up).</summary>
    public bool GrandTotal { get; set; } = true;

    /// <summary>Options for the built-in sample data (the fields of <see cref="ReportStudio.Core.Data.SampleData"/>).</summary>
    public static WizardOptions ForSampleData() => new()
    {
        Name = "Sales list",
        DataSource = new DataSourceDefinition { Provider = DbProvider.Sample },
        Fields = new List<FieldDefinition>
        {
            new() { Name = "Country" }, new() { Name = "Customer" },
            new() { Name = "InvoiceNo", Type = FieldType.Number }, new() { Name = "InvoiceDate", Type = FieldType.Date },
            new() { Name = "Product" }, new() { Name = "Quantity", Type = FieldType.Number },
            new() { Name = "UnitPrice", Type = FieldType.Number }, new() { Name = "Amount", Type = FieldType.Number }
        }
    };
}

/// <summary>
/// Builds a finished report definition from <see cref="WizardOptions"/>: header, column titles, groups with headers and totals, detail rows,
/// grand total and page footer, all sized to the page. The result is an ordinary report that the designer opens and edits.
/// </summary>
public static class ReportWizard
{
    private const double Gap = 6;

    /// <summary>The reasons the options cannot be built (empty = fine), each a sentence for the person using the wizard.</summary>
    public static IReadOnlyList<string> Check(WizardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Resolve(options, out var problems);
        return problems;
    }

    /// <summary>Builds the report. Throws <see cref="InvalidOperationException"/> with the first problem of <see cref="Check"/>.</summary>
    public static ReportDefinition Build(WizardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var plan = Resolve(options, out var problems);
        if (problems.Count > 0) throw new InvalidOperationException(problems[0]);

        var pal = PaletteOf(options.Style);
        var (pageW, pageH) = PageSize(options);
        string name = string.IsNullOrWhiteSpace(options.Name) ? "New report" : options.Name.Trim();

        var def = new ReportDefinition { Name = name, Title = string.IsNullOrWhiteSpace(options.Title) ? name : options.Title.Trim() };
        def.Page.Width = pageW;
        def.Page.Height = pageH;
        def.DataSource = CloneSource(options.DataSource);
        if (def.DataSource.Fields.Count == 0)
            def.DataSource.Fields = plan.All.Select(f => new FieldDefinition { Name = f.Name.Trim(), Type = f.Type }).ToList();

        double w = def.Page.ContentWidth;
        bool table = options.Layout == WizardLayout.Table;
        var cols = table ? LayoutColumns(plan.Columns, w) : new List<Col>();

        // ---- Report header ----
        def.ReportHeader.Height = 64;
        def.ReportHeader.Items.Add(T(0, 4, options.PrintedDate ? w - 210 : w, 30, def.Title, size: 18, bold: true, color: pal.Title));
        if (options.PrintedDate)
            def.ReportHeader.Items.Add(T(w - 200, 10, 200, 18, "Printed {Format(Now, 'dd-MMM-yyyy HH:mm')}", align: HAlign.Right, color: pal.Muted));
        def.ReportHeader.Items.Add(Line(0, 60, w, pal.Line));

        // ---- Page header: the column titles ----
        if (table)
        {
            def.PageHeader.Height = 26;
            foreach (var c in cols)
                def.PageHeader.Items.Add(T(c.X, 4, c.Width, 18, Humanize(c.Field.Name), bold: true, color: pal.HeaderText, back: pal.HeaderBack,
                    align: AlignOf(c.Field)));
            if (pal.HeaderBack.Length == 0) def.PageHeader.Items.Add(Line(0, 25, w, pal.Line));
        }
        else
        {
            def.PageHeader.Height = 0;
        }

        // ---- Groups ----
        for (int i = 0; i < plan.Groups.Count; i++)
        {
            var field = plan.Groups[i];
            var group = new GroupDefinition { Expression = Ref(field.Name) };
            double indent = 10 * i;

            group.Header.Height = i == 0 ? 28 : 22;
            group.Header.Items.Add(T(indent, 4, w - indent, 20, "{" + Ref(field.Name) + "}", format: FormatOf(field),
                size: Math.Max(9, 12 - i), bold: true, color: pal.Title, back: pal.GroupBack));

            if (plan.Totals.Count > 0)
            {
                string label = "Total {" + Ref(field.Name) + "}";
                group.Footer.Height = table ? 30 : FormFooterHeight(plan.Totals.Count, 6);
                if (table) TotalsRow(group.Footer, cols, plan.Totals, 2, 6, 18, 9, label, pal);
                else FormTotals(group.Footer, w, plan.Totals, 6, 9, "Total ", pal);
            }
            else
            {
                group.Footer.Height = 0;
            }
            def.Groups.Add(group);
        }

        // ---- Detail ----
        if (table)
        {
            def.Detail.Height = 20;
            foreach (var c in cols)
                def.Detail.Items.Add(T(c.X, 1, c.Width, 18, "{" + Ref(c.Field.Name) + "}", format: FormatOf(c.Field), align: AlignOf(c.Field)));
            if (pal.RowLines) def.Detail.Items.Add(Line(0, 19, w, pal.Line));
        }
        else
        {
            int n = plan.Columns.Count;
            def.Detail.Height = 20 * n + 10;
            for (int j = 0; j < n; j++)
            {
                var f = plan.Columns[j];
                def.Detail.Items.Add(T(0, 2 + 20 * j, 140, 18, Humanize(f.Name), bold: true, color: pal.Muted));
                def.Detail.Items.Add(T(150, 2 + 20 * j, w - 150, 18, "{" + Ref(f.Name) + "}", format: FormatOf(f), align: HAlign.Left));
            }
            def.Detail.Items.Add(Line(0, def.Detail.Height - 3, w, pal.Line));
        }

        // ---- Report footer ----
        if (!options.GrandTotal)
        {
            def.ReportFooter.Height = 0;
        }
        else if (plan.Totals.Count > 0)
        {
            if (table)
            {
                def.ReportFooter.Height = 40;
                TotalsRow(def.ReportFooter, cols, plan.Totals, 4, 10, 22, 11, "Grand total", pal);
            }
            else
            {
                def.ReportFooter.Height = FormFooterHeight(plan.Totals.Count, 10);
                FormTotals(def.ReportFooter, w, plan.Totals, 10, 11, "Grand total ", pal);
            }
        }
        else
        {
            def.ReportFooter.Height = 36;
            def.ReportFooter.Items.Add(Line(w - 300, 4, 300, pal.Line));
            def.ReportFooter.Items.Add(T(w - 300, 10, 300, 22, "Records: {Count()}", size: 11, bold: true, align: HAlign.Right));
        }

        // ---- Page footer ----
        if (options.PageNumbers)
        {
            def.PageFooter.Height = 24;
            def.PageFooter.Items.Add(Line(0, 2, w, pal.Line));
            def.PageFooter.Items.Add(T(0, 6, w - 210, 16, def.Title, size: 8, color: pal.Muted));
            def.PageFooter.Items.Add(T(w - 200, 6, 200, 16, "Page {Page} of {PageCount}", size: 8, align: HAlign.Right, color: pal.Muted));
        }
        else
        {
            def.PageFooter.Height = 0;
        }

        def.Normalize();
        return def;
    }

    /// <summary>A field name as a column title: "InvoiceNo" and "invoice_no" become "Invoice No" and "Invoice no".</summary>
    public static string Humanize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim().Replace('_', ' ');
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c) && s[i - 1] != ' ')
            {
                bool lowerBefore = char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]);
                bool acronymEnds = char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                if (lowerBefore || acronymEnds) sb.Append(' ');
            }
            sb.Append(c);
        }
        var text = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>How a field is written in an expression: as it is, or in square brackets when it is not a plain name.</summary>
    public static string Ref(string name)
    {
        name = name.Trim();
        bool plain = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
        return plain ? name : "[" + name + "]";
    }

    // ---------------------------------------------------------------------------------------

    private sealed record Plan(List<FieldDefinition> All, List<FieldDefinition> Columns, List<FieldDefinition> Groups, List<FieldDefinition> Totals);

    private sealed record Col(FieldDefinition Field, double X, double Width);

    private sealed record Palette(string Title, string HeaderBack, string HeaderText, string GroupBack, string Muted, string Line, bool RowLines);

    private static Palette PaletteOf(WizardStyle style) => style switch
    {
        WizardStyle.Simple => new("#111827", "#E5E7EB", "#111827", "#F3F4F6", "#6B7280", "#D1D5DB", false),
        WizardStyle.Modern => new("#0F766E", "#0F766E", "#FFFFFF", "#E0F2F1", "#4B5563", "#99C9C4", false),
        WizardStyle.Classic => new("#000000", "", "#000000", "", "#444444", "#000000", true),
        _ => new("#1F3A5F", "#1F3A5F", "#FFFFFF", "#E6EEF7", "#555555", "#9AA5B1", false)
    };

    private static (double Width, double Height) PageSize(WizardOptions o)
    {
        double w = o.Paper == WizardPaper.Letter ? 816 : 794;
        double h = o.Paper == WizardPaper.Letter ? 1056 : 1123;
        return o.Landscape ? (h, w) : (w, h);
    }

    private static double Natural(FieldType type) => type switch
    {
        FieldType.Number => 90,
        FieldType.Date => 85,
        FieldType.Boolean => 60,
        _ => 150
    };

    private static string FormatOf(FieldDefinition f) => f.Type switch
    {
        FieldType.Number => "N2",
        FieldType.Date => "dd-MMM-yyyy",
        _ => ""
    };

    private static HAlign AlignOf(FieldDefinition f) => f.Type == FieldType.Number ? HAlign.Right : HAlign.Left;

    private static bool UsableName(string name) => name.All(ch => !char.IsControl(ch) && ch is not ('{' or '}' or '[' or ']'));

    private static Plan Resolve(WizardOptions o, out List<string> problems)
    {
        var issues = new List<string>();
        problems = issues;
        var all = new List<FieldDefinition>();
        var byName = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in o.Fields ?? new List<FieldDefinition>())
        {
            if (f == null || string.IsNullOrWhiteSpace(f.Name)) continue;
            var name = f.Name.Trim();
            if (!UsableName(name))
            {
                issues.Add($"The field name '{name}' cannot be used: it contains a brace, a bracket or a control character.");
                continue;
            }
            if (byName.TryAdd(name, f)) all.Add(f);
        }
        if (all.Count == 0 && issues.Count == 0) issues.Add("Add the data fields first.");

        foreach (var (text, what) in new[] { (o.Name, "The name"), (o.Title, "The title") })
            if (text != null && (text.Contains('{') || text.Contains('}')))
                issues.Add($"{what} cannot contain {{ or }}.");

        List<FieldDefinition> Pick(IEnumerable<string>? names, string what)
        {
            var picked = new List<FieldDefinition>();
            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                var n = (raw ?? "").Trim();
                if (n.Length == 0) continue;
                if (!byName.TryGetValue(n, out var found)) { issues.Add($"{what}: the field '{n}' is not in the data."); continue; }
                if (!picked.Contains(found)) picked.Add(found);
            }
            return picked;
        }

        var chosen = o.Columns is { Count: > 0 } ? Pick(o.Columns, "Columns") : new List<FieldDefinition>(all);
        var groups = Pick(o.GroupBy, "Group by");
        if (groups.Count > WizardOptions.MaxGroups) issues.Add($"Group by at most {WizardOptions.MaxGroups} fields.");

        var columns = chosen.Where(f => !groups.Contains(f)).ToList();
        if (columns.Count == 0 && all.Count > 0 && issues.Count == 0)
            issues.Add("Every field is used to group the report: keep at least one field as a column.");

        var totals = Pick(o.TotalFields, "Totals");
        foreach (var t in totals)
            if (t.Type != FieldType.Number || !columns.Contains(t))
                issues.Add($"Totals: '{t.Name.Trim()}' must be a number field that is shown in the report.");

        if (o.Layout == WizardLayout.Table && columns.Count > 0)
        {
            double content = PageSize(o).Width - 80;
            double room = content - Gap * (columns.Count - 1);
            if (columns.Sum(c => Natural(c.Type)) > room * 2)
                issues.Add("There are too many columns for one page: choose fewer fields or use landscape.");
        }

        return new Plan(all, columns, groups, totals);
    }

    private static List<Col> LayoutColumns(List<FieldDefinition> fields, double contentWidth)
    {
        double room = contentWidth - Gap * (fields.Count - 1);
        var natural = fields.Select(f => Natural(f.Type)).ToArray();
        double sum = natural.Sum();
        var widths = new double[fields.Count];

        if (sum >= room)
        {
            for (int i = 0; i < widths.Length; i++) widths[i] = natural[i] * room / sum;
        }
        else
        {
            double extra = room - sum;
            int texts = fields.Count(f => f.Type == FieldType.Text);
            for (int i = 0; i < widths.Length; i++)
                widths[i] = natural[i] + (texts > 0 ? (fields[i].Type == FieldType.Text ? extra / texts : 0) : extra / fields.Count);
        }

        var result = new List<Col>();
        double x = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            double width = Math.Round(widths[i], 2);
            result.Add(new Col(fields[i], Math.Round(x, 2), width));
            x += width + Gap;
        }
        return result;
    }

    private static void TotalsRow(Band band, List<Col> cols, List<FieldDefinition> totals, double lineY, double textY, double textH,
        double size, string label, Palette pal)
    {
        var under = cols.Where(c => totals.Contains(c.Field)).ToList();
        double first = under.Min(c => c.X);
        if (first > 90) band.Items.Add(T(0, textY, first - Gap, textH, label, size: size, bold: true, align: HAlign.Right));
        band.Items.Add(Line(first, lineY, Math.Max(1, cols.Max(c => c.X + c.Width) - first), pal.Line));
        foreach (var c in under)
            band.Items.Add(T(c.X, textY, c.Width, textH, "{Sum(" + Ref(c.Field.Name) + ")}", format: "N2", size: size, bold: true, align: HAlign.Right));
    }

    private static double FormFooterHeight(int rows, double top) => top + 20 * rows + 4;

    private static void FormTotals(Band band, double w, List<FieldDefinition> totals, double top, double size, string prefix, Palette pal)
    {
        band.Items.Add(Line(w - 300, 2, 300, pal.Line));
        for (int j = 0; j < totals.Count; j++)
        {
            double y = top + 20 * j;
            band.Items.Add(T(w - 300, y, 170, 18, prefix + Humanize(totals[j].Name), size: size, bold: true, align: HAlign.Right));
            band.Items.Add(T(w - 120, y, 120, 18, "{Sum(" + Ref(totals[j].Name) + ")}", format: "N2", size: size, bold: true, align: HAlign.Right));
        }
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static DataSourceDefinition CloneSource(DataSourceDefinition? source) =>
        source == null ? new DataSourceDefinition()
            : JsonSerializer.Deserialize<DataSourceDefinition>(JsonSerializer.Serialize(source, Json), Json) ?? new DataSourceDefinition();

    internal static ReportItem T(double x, double y, double w, double h, string text, string format = "",
        double size = 9, bool bold = false, bool italic = false, HAlign align = HAlign.Left,
        string color = "#000000", string back = "") => new()
    {
        Kind = ItemKind.Text, X = x, Y = y, Width = w, Height = h, Text = text, Format = format,
        FontSize = size, Bold = bold, Italic = italic, HAlign = align, ForeColor = color, BackColor = back,
        VAlign = VAlign.Middle, WordWrap = false
    };

    internal static ReportItem Line(double x, double y, double w, string color = "#9AA5B1") => new()
    {
        Kind = ItemKind.Line, X = x, Y = y, Width = w, Height = 0, ForeColor = color, BorderWidth = 1
    };
}
