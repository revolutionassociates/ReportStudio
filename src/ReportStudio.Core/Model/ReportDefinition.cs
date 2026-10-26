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
    public DataSourceDefinition DataSource { get; set; } = new();
    public List<ReportParameter> Parameters { get; set; } = new();
    public List<GroupDefinition> Groups { get; set; } = new();

    /// <summary>Optional sort expressions applied after grouping keys, e.g. "OrderDate".</summary>
    public List<string> SortBy { get; set; } = new();

    public Band ReportHeader { get; set; } = new() { Kind = BandKind.ReportHeader, Height = 60 };
    public Band PageHeader { get; set; } = new() { Kind = BandKind.PageHeader, Height = 30 };
    public Band Detail { get; set; } = new() { Kind = BandKind.Detail, Height = 22 };
    public Band ReportFooter { get; set; } = new() { Kind = BandKind.ReportFooter, Height = 30 };
    public Band PageFooter { get; set; } = new() { Kind = BandKind.PageFooter, Height = 28 };

    /// <summary>True when running this report would execute a query taken from the file (needs the user's trust).</summary>
    [JsonIgnore]
    public bool HasOwnQuery =>
        DataSource.Provider is not (DbProvider.Sample or DbProvider.Host) && !string.IsNullOrWhiteSpace(DataSource.Query);

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

    /// <summary>Makes Kind values and names consistent after loading or editing.</summary>
    public void Normalize()
    {
        // Host reports get their rows from the host application: whatever query or connection string a file
        // carries is ignored (and therefore dropped), so a crafted file can never make the host open a connection.
        if (DataSource.Provider == DbProvider.Host)
        {
            DataSource.Query = "";
            DataSource.ConnectionString = "";
        }

        ReportHeader.Kind = BandKind.ReportHeader;
        PageHeader.Kind = BandKind.PageHeader;
        Detail.Kind = BandKind.Detail;
        ReportFooter.Kind = BandKind.ReportFooter;
        PageFooter.Kind = BandKind.PageFooter;
        for (int i = 0; i < Groups.Count; i++)
        {
            var g = Groups[i];
            g.Header.Kind = BandKind.GroupHeader;
            g.Footer.Kind = BandKind.GroupFooter;
            g.Header.Name = $"Group Header {i + 1}";
            g.Footer.Name = $"Group Footer {i + 1}";
        }
    }
}

public class PageSettings
{
    public double Width { get; set; } = 794;   // A4 portrait @96dpi
    public double Height { get; set; } = 1123;
    public double MarginLeft { get; set; } = 40;
    public double MarginRight { get; set; } = 40;
    public double MarginTop { get; set; } = 40;
    public double MarginBottom { get; set; } = 40;

    [JsonIgnore] public double ContentWidth => Width - MarginLeft - MarginRight;
    [JsonIgnore] public bool IsLandscape => Width > Height;

    public void SetA4(bool landscape)
    {
        Width = landscape ? 1123 : 794;
        Height = landscape ? 794 : 1123;
    }
}

public enum BandKind { ReportHeader, PageHeader, GroupHeader, Detail, GroupFooter, ReportFooter, PageFooter }

public class Band
{
    [ReadOnly(true)]
    public BandKind Kind { get; set; }
    public string Name { get; set; } = "";
    public double Height { get; set; } = 24;

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
    /// Each instance of this group starts on a new page and restarts page numbering, so {Page} and {PageCount}
    /// count only that instance's pages (e.g. "Page 1 of 1" for every employee's payslip).
    /// </summary>
    public bool RestartPageNumbers { get; set; }
    public Band Header { get; set; } = new() { Kind = BandKind.GroupHeader, Height = 26 };
    public Band Footer { get; set; } = new() { Kind = BandKind.GroupFooter, Height = 26 };
}

public enum ParameterType { Text, Number, Date, Boolean }

public class ReportParameter
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public ParameterType Type { get; set; } = ParameterType.Text;
    public string DefaultValue { get; set; } = "";

    public object? Parse(string? raw)
    {
        raw ??= DefaultValue;
        if (string.IsNullOrWhiteSpace(raw)) return null;
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
public enum DbProvider { Sample, SqlServer, MySql, PostgreSql, Host }

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
