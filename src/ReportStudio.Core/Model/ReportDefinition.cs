using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReportStudio.Core.Model;

/// <summary>
/// The complete, serialisable description of a report (stored as a .rsr JSON file).
/// All geometry is in device-independent pixels at 96 DPI (A4 = 794 x 1123).
/// </summary>
public class ReportDefinition
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled Report";
    public string Title { get; set; } = "";

    public PageSettings Page { get; set; } = new();

    /// <summary>Faint text behind the content of every page (DRAFT, CONFIDENTIAL, ...). Empty text = none.</summary>
    public WatermarkSettings Watermark { get; set; } = new();

    /// <summary>A colour, or a two-colour gradient, behind the whole of every page. No colour = the plain page.</summary>
    public PageBackgroundSettings Background { get; set; } = new();

    public DataSourceDefinition DataSource { get; set; } = new();

    /// <summary>Further named sets of rows (requirement 39), read with Lookup, LookupSum, LookupCount and LookupList or joined onto the main rows.</summary>
    public List<DataSetDefinition> DataSets { get; set; } = new();

    /// <summary>Data sets whose fields are added to the main rows before the report filters, sorts and groups them; applied in this order.</summary>
    public List<JoinDefinition> Joins { get; set; } = new();
    public List<ReportParameter> Parameters { get; set; } = new();
    public List<GroupDefinition> Groups { get; set; } = new();

    /// <summary>Optional sort expressions applied after grouping keys, e.g. "OrderDate".</summary>
    public List<string> SortBy { get; set; } = new();

    /// <summary>
    /// Record filter: only rows for which this expression is true are used, e.g. "Status == 'Active' &amp;&amp; Net &gt; 0".
    /// Applied before grouping, totals and printing, so every total counts only the kept rows. Empty = all rows.
    /// </summary>
    public string Filter { get; set; } = "";

    /// <summary>Running totals and shared values, updated as detail rows print and shown as {Name} in any band.</summary>
    public List<VariableDefinition> Variables { get; set; } = new();

    public Band ReportHeader { get; set; } = new() { Kind = BandKind.ReportHeader, Height = 60 };
    public Band PageHeader { get; set; } = new() { Kind = BandKind.PageHeader, Height = 30 };
    public Band Detail { get; set; } = new() { Kind = BandKind.Detail, Height = 22 };
    public Band ReportFooter { get; set; } = new() { Kind = BandKind.ReportFooter, Height = 30 };
    public Band PageFooter { get; set; } = new() { Kind = BandKind.PageFooter, Height = 28 };

    /// <summary>True when running this report would execute a query taken from the file (needs the user's trust).</summary>
    [JsonIgnore]
    public bool HasOwnQuery =>
        DataSource.ReadsExternalData
        || (DataSource.Provider.IsDatabase() && Parameters.Any(p => !string.IsNullOrWhiteSpace(p.ChoicesQuery)))
        || DataSets.Any(d => d.DataSource.ReadsExternalData);

    /// <summary>True when the report (or one of its data sets) reads a JSON, XML, CSV or Excel file.</summary>
    [JsonIgnore]
    public bool UsesFileSources =>
        (DataSource.Provider.IsFile() && DataSource.ReadsExternalData) || DataSets.Any(d => d.DataSource.Provider.IsFile() && d.DataSource.ReadsExternalData);

    /// <summary>True when the report (or one of its data sets) reads a REST or OData web address.</summary>
    [JsonIgnore]
    public bool UsesWebSources =>
        (DataSource.Provider.IsWeb() && DataSource.ReadsExternalData) || DataSets.Any(d => d.DataSource.Provider.IsWeb() && d.DataSource.ReadsExternalData);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Serialises the report. Host reports are normalised first, so they never carry a query or connection string.</summary>
    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static ReportDefinition FromJson(string json)
    {
        var def = JsonSerializer.Deserialize<ReportDefinition>(json, JsonOptions)
                  ?? throw new InvalidDataException("The report file is empty or invalid.");
        def.Normalize();
        return def;
    }

    public static ReportDefinition Load(string path) => FromJson(File.ReadAllText(path));

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public ReportDefinition Clone() => FromJson(ToJson());

    /// <summary>Every band in design/render order (group bands included).</summary>
    public IEnumerable<Band> AllBands()
    {
        yield return ReportHeader;
        yield return PageHeader;
        foreach (var g in Groups) yield return g.Header;
        yield return Detail;
        for (int i = Groups.Count - 1; i >= 0; i--) yield return Groups[i].Footer;
        yield return ReportFooter;
        yield return PageFooter;
    }

    public static bool IsValidVariableName(string? name) =>
        !string.IsNullOrEmpty(name) && !char.IsDigit(name[0]) && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    /// <summary>
    /// For a host that owns all data access: the report's rows and every one of its data sets come from the host (named data sets by
    /// <c>ReportOptions.DataSets</c>), whatever query, file or web address the report file carries.
    /// </summary>
    public void UseHostDataOnly()
    {
        DataSource.Provider = DbProvider.Host;
        DataSets ??= new();
        foreach (var d in DataSets.Where(d => d != null))
        {
            d.DataSource ??= new();
            d.DataSource.Provider = DbProvider.Host;
        }
        Normalize();
    }

    /// <summary>Makes Kind values and names consistent after loading or editing.</summary>
    public void Normalize()
    {
        DataSets ??= new();
        Joins ??= new();
        DataSets.RemoveAll(d => d == null);
        Joins.RemoveAll(j => j == null);
        foreach (var d in DataSets) d.Normalize();
        foreach (var j in Joins) j.Normalize();

        // Host reports get their rows from the host application: whatever query or connection string a file
        // carries is ignored (and therefore dropped), so a crafted file can never make the host open a connection.
        if (DataSource.Provider == DbProvider.Host)
        {
            DataSource.Query = "";
            DataSource.ConnectionString = "";
            DataSource.Source = "";
            DataSource.Headers = "";
        }

        Watermark ??= new WatermarkSettings();      // a file may carry "Watermark": null
        Watermark.Normalize();
        Background ??= new PageBackgroundSettings();
        Background.Normalize();

        // A variable needs a usable name; the first of two with the same name wins.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Variables.RemoveAll(v => v == null || !IsValidVariableName(v.Name) || !seen.Add(v.Name));
        foreach (var v in Variables) v.ResetGroup = Math.Max(1, v.ResetGroup);

        ReportHeader.Kind = BandKind.ReportHeader;
        PageHeader.Kind = BandKind.PageHeader;
        Detail.Kind = BandKind.Detail;
        ReportFooter.Kind = BandKind.ReportFooter;
        PageFooter.Kind = BandKind.PageFooter;
        for (int i = 0; i < Groups.Count; i++)
        {
            var g = Groups[i];
            g.TopN = Math.Max(0, g.TopN);
            g.KeepHeaderWithRows = Math.Max(0, g.KeepHeaderWithRows);
            g.KeepFooterWithRows = Math.Max(0, g.KeepFooterWithRows);
            g.Header.Kind = BandKind.GroupHeader;
            g.Footer.Kind = BandKind.GroupFooter;
            g.Header.Name = $"Group Header {i + 1}";
            g.Footer.Name = $"Group Footer {i + 1}";
        }
    }
}

public enum ColumnOrder { Down, Across }

public class PageSettings
{
    public double Width { get; set; } = 794;   // A4 portrait @96dpi
    public double Height { get; set; } = 1123;
    public double MarginLeft { get; set; } = 40;
    public double MarginRight { get; set; } = 40;
    public double MarginTop { get; set; } = 40;
    public double MarginBottom { get; set; } = 40;

    /// <summary>Most columns a page can have.</summary>
    public const int MaxColumns = 12;

    /// <summary>Columns of detail rows on a page (1 = the usual single column). Group headers and footers, the report header and footer and the page header and footer stay full width.</summary>
    public int Columns { get; set; } = 1;

    /// <summary>Space between two columns, in report pixels.</summary>
    public double ColumnGap { get; set; } = 20;

    /// <summary>Down: a column is filled to the bottom of the page before the next one starts (a phone book). Across: rows go left to right, then the next line (address labels).</summary>
    public ColumnOrder ColumnOrder { get; set; } = ColumnOrder.Down;

    [JsonIgnore] public double ContentWidth => Width - MarginLeft - MarginRight;
    [JsonIgnore] public bool IsLandscape => Width > Height;

    /// <summary>The number of columns, kept between 1 and <see cref="MaxColumns"/>.</summary>
    [JsonIgnore] public int ColumnCount => Math.Clamp(Columns, 1, MaxColumns);

    [JsonIgnore] private double Gap => Math.Max(0, ColumnGap);

    /// <summary>The width of one column: the page's content width less the gaps, shared equally.</summary>
    [JsonIgnore] public double ColumnWidth => Math.Max(1, (ContentWidth - Gap * (ColumnCount - 1)) / ColumnCount);

    /// <summary>A copy of these settings with another page size (everything else, margins and columns included, is kept).</summary>
    public PageSettings WithSize(double width, double height) => new()
    {
        Width = width, Height = height,
        MarginLeft = MarginLeft, MarginRight = MarginRight, MarginTop = MarginTop, MarginBottom = MarginBottom,
        Columns = Columns, ColumnGap = ColumnGap, ColumnOrder = ColumnOrder
    };

    /// <summary>Where a column starts, counted from the left margin.</summary>
    public double ColumnLeft(int column) => Math.Clamp(column, 0, ColumnCount - 1) * (ColumnWidth + Gap);

    public void SetA4(bool landscape)
    {
        Width = landscape ? 1123 : 794;
        Height = landscape ? 794 : 1123;
    }
}

/// <summary>The colour behind a whole page: one colour, or a gradient from it to a second colour.</summary>
public class PageBackgroundSettings
{
    /// <summary>Hex colour such as #FFF8E1. Empty = no background (the plain page).</summary>
    public string Color { get; set; } = "";

    /// <summary>Second colour (hex) of a gradient. Empty = a single colour. Needs <see cref="Color"/>.</summary>
    public string GradientColor { get; set; } = "";

    public GradientDirection Direction { get; set; }

    public void Normalize()
    {
        Color = (Color ?? "").Trim();
        GradientColor = (GradientColor ?? "").Trim();
    }
}

/// <summary>Text printed faintly behind the content of every page, turned to any angle: DRAFT, CONFIDENTIAL, a status from a parameter.</summary>
public class WatermarkSettings
{
    public const string DefaultColor = "#9CA3AF";

    /// <summary>The words, with {expressions} like any other text (e.g. {@Status}). Empty = no watermark.</summary>
    public string Text { get; set; } = "";

    /// <summary>Hex colour such as #9CA3AF.</summary>
    public string Color { get; set; } = DefaultColor;

    /// <summary>0 = invisible, 1 = solid.</summary>
    public double Opacity { get; set; } = 0.3;

    /// <summary>Degrees, turned counter-clockwise: 0 = straight along the page, 45 = rising to the right.</summary>
    public double Angle { get; set; } = 45;

    /// <summary>Font size in points. Text that would run off the page is made smaller so it always fits.</summary>
    public double FontSize { get; set; } = 72;

    public bool Bold { get; set; } = true;

    /// <summary>
    /// A picture kept in the report file (base64 of a PNG, JPEG, GIF, BMP, TIFF or plain SVG), printed behind the content like the text,
    /// with the same opacity and angle. Empty = no picture. Text and picture can be used together.
    /// </summary>
    public string ImageBase64 { get; set; } = "";

    /// <summary>How wide the picture may be, in percent of the page width (it is made smaller when, turned, it would not fit on the page).</summary>
    public double ImageSize { get; set; } = DefaultImageSize;

    public const double DefaultImageSize = 50;

    /// <summary>Puts every value in its usable range (a file may carry anything).</summary>
    public void Normalize()
    {
        ImageBase64 = (ImageBase64 ?? "").Trim();
        ImageSize = double.IsFinite(ImageSize) ? Math.Clamp(ImageSize, 5, 100) : DefaultImageSize;
        Text ??= "";
        Color = string.IsNullOrWhiteSpace(Color) ? DefaultColor : Color.Trim();
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 0.3;
        Angle = double.IsFinite(Angle) ? Math.Clamp(Angle, -360, 360) : 45;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 6, 600) : 72;
    }
}

public enum BandKind { ReportHeader, PageHeader, GroupHeader, Detail, GroupFooter, ReportFooter, PageFooter }

public class Band
{
    [ReadOnly(true)]
    public BandKind Kind { get; set; }
    public string Name { get; set; } = "";
    public double Height { get; set; } = 24;

    /// <summary>The band is not printed at all when nothing in it prints (every element is hidden, blank or suppressed).</summary>
    public bool SuppressIfBlank { get; set; }

    /// <summary>The band is only as tall as what prints in it, after elements have moved up (see ReportItem.CanShrink).</summary>
    public bool CanShrink { get; set; }

    [Browsable(false)]
    public List<ReportItem> Items { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Kind switch
    {
        BandKind.ReportHeader => "Report Header",
        BandKind.PageHeader => "Page Header",
        BandKind.GroupHeader => "Group Header",
        BandKind.Detail => "Details",
        BandKind.GroupFooter => "Group Footer",
        BandKind.ReportFooter => "Report Footer",
        BandKind.PageFooter => "Page Footer",
        _ => Kind.ToString()
    };
}

public class GroupDefinition
{
    /// <summary>Expression whose value changes between groups, e.g. "Country".</summary>
    public string Expression { get; set; } = "";
    public bool StartNewPage { get; set; }

    /// <summary>
    /// The page size (report pixels, 96 per inch) of the pages this group starts, for a group that starts a new page: an appendix in landscape,
    /// a wide table on A3. 0 = no change: an outermost group goes back to the report's own page size, an inner group keeps the size its
    /// outer group set. The margins and columns stay the report's. Ignored for a group that does not start a new page.
    /// </summary>
    public double PageWidth { get; set; }

    /// <summary>See <see cref="PageWidth"/>.</summary>
    public double PageHeight { get; set; }

    /// <summary>
    /// Makes the page size above depend on the group: an expression worked out over the group's first row, such as <c>Section == 'Appendix'</c>.
    /// The size applies to the pages of the groups for which it is true; the others get the usual page. Empty = it applies to every group.
    /// </summary>
    public string PageSizeWhen { get; set; } = "";

    /// <summary>
    /// Each instance of this group starts on a new page and restarts page numbering, so {Page} and {PageCount}
    /// count only that instance's pages (e.g. "Page 1 of 1" for every employee's payslip).
    /// </summary>
    public bool RestartPageNumbers { get; set; }

    /// <summary>
    /// Orders this group's instances by a value worked out over each one, e.g. "Sum(Amount)" puts customers in order of
    /// sales. Empty = in the order of the group expression. Ties keep that order.
    /// </summary>
    public string SortExpression { get; set; } = "";

    /// <summary>True = largest first (usual for totals). False = smallest first.</summary>
    public bool SortDescending { get; set; }

    /// <summary>Keep only the first N instances after ordering (Top 10 customers). 0 = all. Totals above count only those kept.</summary>
    public int TopN { get; set; }

    /// <summary>
    /// Keep each instance of this group on one page: when it does not fit in the space left, it starts on a new page.
    /// A group taller than a whole page flows as usual.
    /// </summary>
    public bool KeepTogether { get; set; }

    /// <summary>
    /// The header is never left alone at the bottom of a page: it moves to the next page unless this many detail rows
    /// (and the headers of inner groups) fit after it. 0 = off.
    /// </summary>
    public int KeepHeaderWithRows { get; set; }

    /// <summary>
    /// The footer is never left alone at the top of a page: this many of the last detail rows move to the next page
    /// together with it. 0 = off. Applies to the group nearest the detail rows.
    /// </summary>
    public int KeepFooterWithRows { get; set; }

    /// <summary>
    /// When an instance of this group continues on a new page, print its header again at the top of that page.
    /// In the header, {Continued} is true on those repeats, e.g. IIf(Continued, '(continued)', '').
    /// </summary>
    public bool RepeatHeader { get; set; }

    /// <summary>
    /// Drill-down: in the viewer, clicking this group's header hides or shows its rows (and inner groups); the footer stays.
    /// In the header, {DrillMark} is "-" while open and "+" while closed, e.g. in a small text element at the left.
    /// </summary>
    public bool Collapsible { get; set; }

    /// <summary>With <see cref="Collapsible"/>: every instance starts closed; the viewer opens the ones the user clicks.</summary>
    public bool StartCollapsed { get; set; }

    public Band Header { get; set; } = new() { Kind = BandKind.GroupHeader, Height = 26 };
    public Band Footer { get; set; } = new() { Kind = BandKind.GroupFooter, Height = 26 };
}

public enum ParameterType { Text, Number, Date, Boolean }

public class ReportParameter
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public ParameterType Type { get; set; } = ParameterType.Text;

    /// <summary>
    /// The value offered first. Starting with = it is an expression worked out each time the report is run, e.g.
    /// <c>=MonthStart(Today())</c> or <c>=FinancialYear(Today())</c>, so the date moves on by itself; the user reviews it in
    /// the parameter window before running.
    /// </summary>
    public string DefaultValue { get; set; } = "";

    /// <summary>A fixed drop-down list: <c>Active;Inactive</c> or <c>A=Active;I=Inactive</c> (value first, shown text second).</summary>
    public string Choices { get; set; } = "";

    /// <summary>
    /// A drop-down list read from the database: a query returning the value (first column) and optionally the shown text
    /// (second). It runs on the report's connection and may use other parameters as @Name.
    /// </summary>
    public string ChoicesQuery { get; set; } = "";

    /// <summary>Parameters (separated by ;) the choices query uses: the list is read again when one of them changes.</summary>
    public string DependsOn { get; set; } = "";

    /// <summary>The user may pick several values. The value is then a list: <c>In(Department, @Depts)</c> or SQL <c>IN (@Depts)</c>.</summary>
    public bool MultiValue { get; set; }

    /// <summary>The default as text: the plain default, or the result of its expression.</summary>
    public string ResolveDefault(DateTime? now = null)
    {
        var text = DefaultValue ?? "";
        if (!text.StartsWith('=')) return text;
        try
        {
            var value = ReportStudio.Core.Expressions.Expr.Evaluate(text.Substring(1),
                new ReportStudio.Core.Expressions.EvalContext { Now = now ?? DateTime.Now, Culture = System.Globalization.CultureInfo.InvariantCulture });
            return value switch
            {
                null => "",
                DateTime d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? ""
            };
        }
        catch (ReportStudio.Core.Expressions.ExpressionException)
        {
            return "";   // a default that cannot be worked out is simply empty
        }
    }

    public object? Parse(string? raw)
    {
        raw ??= ResolveDefault();
        if (MultiValue) return new ValueList(ValueList.Split(raw).Select(ParseOne));
        return string.IsNullOrWhiteSpace(raw) ? null : ParseOne(raw);
    }

    private object? ParseOne(string raw)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return Type switch
        {
            ParameterType.Number => decimal.Parse(raw, inv),
            ParameterType.Date => DateTime.Parse(raw, inv),
            ParameterType.Boolean => bool.Parse(raw),
            _ => raw
        };
    }
}

/// <summary>
/// Host = the application embedding the engine supplies the rows itself; the report carries no query
/// and no connection string. New values are appended so existing files keep their meaning.
/// </summary>
public enum DbProvider
{
    Sample, SqlServer, MySql, PostgreSql, Host,
    /// <summary>A JSON file (requirement 40).</summary>
    Json,
    /// <summary>An XML file.</summary>
    Xml,
    /// <summary>A delimited text file (comma, semicolon, tab or pipe).</summary>
    Csv,
    /// <summary>An Excel workbook (.xlsx).</summary>
    Excel,
    /// <summary>A web service that returns JSON (HTTP GET).</summary>
    Rest,
    /// <summary>An OData service (JSON; follows paging links).</summary>
    OData
}

public static class DbProviderExtensions
{
    /// <summary>SQL Server, MySQL, PostgreSQL: read by the registered database loader.</summary>
    public static bool IsDatabase(this DbProvider p) => p is DbProvider.SqlServer or DbProvider.MySql or DbProvider.PostgreSql;

    /// <summary>JSON, XML, CSV, Excel: read from a file on disk.</summary>
    public static bool IsFile(this DbProvider p) => p is DbProvider.Json or DbProvider.Xml or DbProvider.Csv or DbProvider.Excel;

    /// <summary>REST and OData: read over HTTP.</summary>
    public static bool IsWeb(this DbProvider p) => p is DbProvider.Rest or DbProvider.OData;
}

public enum FieldType { Text, Number, Date, Boolean }

/// <summary>One field of the report's data: a name and a type.</summary>
public class FieldDefinition
{
    public string Name { get; set; } = "";
    public FieldType Type { get; set; } = FieldType.Text;

    /// <summary>Parses lines like "BasicPay:Number" (type optional, defaults to Text). Blank lines and duplicates are skipped.</summary>
    public static List<FieldDefinition> ParseLines(IEnumerable<string> lines)
    {
        var result = new List<FieldDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var name = line;
            var type = FieldType.Text;
            int colon = line.LastIndexOf(':');
            if (colon > 0 && Enum.TryParse<FieldType>(line[(colon + 1)..].Trim(), ignoreCase: true, out var parsed))
            {
                name = line[..colon].Trim();
                type = parsed;
            }
            if (name.Length > 0 && seen.Add(name)) result.Add(new FieldDefinition { Name = name, Type = type });
        }
        return result;
    }

    public override string ToString() => $"{Name}:{Type}";
}

public class DataSourceDefinition
{
    public DbProvider Provider { get; set; } = DbProvider.Sample;
    public string ConnectionString { get; set; } = "";

    /// <summary>SQL text. Report parameters are referenced as @ParamName and bound as real DB parameters.</summary>
    public string Query { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 60;

    // ---- file and web sources (requirement 40) ----

    /// <summary>The file (JSON, XML, CSV, Excel; relative to the report's folder) or the web address (REST, OData) the rows are read from.</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Where the rows are inside the document. JSON: a path such as <c>data.items</c> or <c>$.orders[0].lines</c> (empty = the document itself);
    /// XML: element names such as <c>orders/order</c>, or <c>//order</c> for every element of that name (empty = the root's children);
    /// Excel: the sheet name (empty = the first sheet); CSV: not used.
    /// </summary>
    public string Selector { get; set; } = "";

    /// <summary>CSV: the separator, one character or "\t" for a tab. Empty = worked out from the first line.</summary>
    public string Delimiter { get; set; } = "";

    /// <summary>CSV and Excel: the first row holds the field names. Without it the fields are Column1, Column2, ...</summary>
    public bool HasHeaderRow { get; set; } = true;

    /// <summary>REST and OData: extra request headers, one per line as "Name: value" (an API key, for example). Never shown in warnings or errors.</summary>
    public string Headers { get; set; } = "";

    /// <summary>
    /// How many seconds the rows may be reused by a later run instead of being read again (0 = always read again). Needs the host or
    /// viewer to keep a <see cref="ReportStudio.Core.Data.DataCache"/>; at most a day. Ignored for the sample data and Host rows.
    /// </summary>
    public int CacheSeconds { get; set; }

    /// <summary>
    /// True when running this source reads something outside the report: a database query, a file, a web address.
    /// Such a report asks for trust when it comes from a file.
    /// </summary>
    [JsonIgnore]
    public bool ReadsExternalData =>
        Provider.IsDatabase() ? !string.IsNullOrWhiteSpace(Query) :
        Provider.IsFile() || Provider.IsWeb() ? !string.IsNullOrWhiteSpace(Source) : false;

    /// <summary>What is read, for the trust prompt: the query for a database, the address or file for the others (secrets in an address hidden).</summary>
    public string DescribeForTrust(int maxChars)
    {
        string text = Provider.IsDatabase() ? $"{Provider}, {MaskedConnectionString}:\n{Query.Trim()}"
            : $"{Provider}: {MaskedSource}" + (string.IsNullOrWhiteSpace(Selector) ? "" : $"\nSelect: {Selector.Trim()}") + HeaderNamesForTrust();
        return text.Length > maxChars ? text[..maxChars] + " …" : text;
    }

    // Only the names of the headers are shown: their values are keys.
    private string HeaderNamesForTrust()
    {
        if (!Provider.IsWeb() || string.IsNullOrWhiteSpace(Headers)) return "";
        var names = Headers.Split('\n').Select(l => l.Split(':')[0].Trim()).Where(n => n.Length > 0).ToList();
        return names.Count == 0 ? "" : "\nSends headers: " + string.Join(", ", names);
    }

    /// <summary>The source with secrets hidden: a user name and password in a web address, and values of parameters called key, token, secret, password and the like.</summary>
    [JsonIgnore]
    public string MaskedSource
    {
        get
        {
            var text = System.Text.RegularExpressions.Regex.Replace(Source ?? "", @"(?i)//[^/@\s]+@", "//***@");
            return System.Text.RegularExpressions.Regex.Replace(text,
                @"(?i)([?&;](?:[\w-]*(?:key|token|secret|password|pwd|sig|signature|auth)[\w-]*)=)[^&;#\s]*", "$1***");
        }
    }

    /// <summary>
    /// Field names and types the report expects. Lets the designer's field list work without a database
    /// (essential for Host reports). Layouts only name fields; the host supplies the values.
    /// </summary>
    public List<FieldDefinition> Fields { get; set; } = new();

    /// <summary>The connection string with any password value hidden, safe to show in a warning dialog.</summary>
    [JsonIgnore]
    public string MaskedConnectionString =>
        System.Text.RegularExpressions.Regex.Replace(ConnectionString ?? "", @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
}
