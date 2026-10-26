using System.Globalization;
using System.Text;
using ReportStudio.Core.Data;
using ReportStudio.Core.Formatting;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Finds files in the repository (samples, golden files) from wherever the tests run.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ReportStudio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("ReportStudio.sln not found above " + AppContext.BaseDirectory);
    }

    public static string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
}

/// <summary>
/// Golden-file check. A missing golden file is created from the current output (commit it);
/// set UPDATE_GOLDEN=1 to overwrite after an intended layout change.
/// </summary>
internal static class Golden
{
    public static void Check(string name, string actual)
    {
        var path = Repo.Combine("tests", "ReportStudio.Tests", "Golden", name + ".txt");
        actual = Normalize(actual);

        if (!File.Exists(path) || Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }
        Assert.Equal(Normalize(File.ReadAllText(path)), actual);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd() + "\n";
}

public class SampleFileTests
{
    private static readonly DateTime FixedNow = new(2026, 10, 31);

    private static (ReportDefinition Def, List<Dictionary<string, object?>> Rows) Load(string name)
    {
        var def = ReportDefinition.Load(Repo.Combine("samples", name + ".rsr"));
        var rows = JsonRows.Load(Repo.Combine("samples", name + ".data.json"), def.DataSource);
        return (def, rows);
    }

    private static ReportResult Run(string name)
    {
        var (def, rows) = Load(name);
        return ReportEngine.Run(def, null, rows, new ReportOptions { Culture = IndianFormat.Culture, Now = FixedNow });
    }

    private static string Dump(ReportResult result)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var page in result.Pages)
        {
            sb.AppendLine(string.Create(inv, $"page {page.Number} section {page.Section} size {page.Width:0.#}x{page.Height:0.#}"));
            foreach (var i in page.Items)
                sb.AppendLine(string.Create(inv,
                    $"  {i.Band}#{i.BandInstance} {i.Kind} x={i.X:0.#} y={i.Y:0.#} w={i.Width:0.#} h={i.Height:0.#} '{i.Text}'"));
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("payslip")]
    [InlineData("form16")]
    public void Sample_is_a_safe_host_report_with_a_field_schema(string name)
    {
        var (def, rows) = Load(name);

        Assert.Equal(DbProvider.Host, def.DataSource.Provider);
        Assert.False(def.HasOwnQuery);
        Assert.Equal("", def.DataSource.ConnectionString);
        Assert.NotEmpty(def.DataSource.Fields);
        Assert.Equal(3, rows.Count);

        // every field the data carries is in the schema, and the other way round
        var schema = def.DataSource.Fields.Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.Equal(schema, rows[0].Keys.OrderBy(n => n).ToList());
    }

    [Theory]
    [InlineData("payslip")]
    [InlineData("form16")]
    public void Each_employee_gets_its_own_page_numbered_1_of_1(string name)
    {
        var result = Run(name);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Pages.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.Pages.Select(p => p.Section));
        Assert.All(result.Pages, p => Assert.Contains(p.Items, i => i.Text == "Page 1 of 1"));
    }

    [Fact]
    public void Payslip_shows_indian_grouping_and_the_net_pay_in_words()
    {
        var texts = Run("payslip").Pages[2].Items.Select(i => i.Text).ToList();   // E003

        Assert.Contains("Meera Iyer", texts);
        Assert.Contains("10-01-2015", texts);          // date of joining, dd-MM-yyyy
        Assert.Contains("10,00,000.50", texts);        // gross earnings
        Assert.Contains("8,10,346.25", texts);         // net pay
        Assert.Contains("Rupees Eight Lakh Ten Thousand Three Hundred Forty Six and Twenty Five Paise Only", texts);
    }

    [Fact]
    public void Form16_shows_the_tax_and_its_words()
    {
        var texts = Run("form16").Pages[0].Items.Select(i => i.Text).ToList();      // E001

        Assert.Contains("Form 16 - Part B (sample layout)", texts);
        Assert.Contains("54,080.00", texts);
        Assert.Contains("Tax deducted in words: Rupees Fifty Four Thousand Eighty Only", texts);
    }

    [Theory]
    [InlineData("payslip")]
    [InlineData("form16")]
    public void Layout_matches_the_golden_file(string name) => Golden.Check(name + ".layout", Dump(Run(name)));
}
