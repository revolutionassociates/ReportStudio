using System.Globalization;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using Xunit;

namespace ReportStudio.Tests;

public class ExpressionTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static EvalContext Ctx(params (string K, object? V)[] row)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in row) d[k] = v;
        return new EvalContext { Row = d, Scope = new[] { (IReadOnlyDictionary<string, object?>)d }, Culture = Inv };
    }

    [Fact]
    public void Arithmetic_respects_precedence()
        => Assert.Equal(14m, Expr.Evaluate("2 + 3 * 4", Ctx()));

    [Fact]
    public void Fields_and_bracketed_fields_resolve()
    {
        var ctx = Ctx(("Qty", 3m), ("Unit Price", 5m));
        Assert.Equal(15m, Expr.Evaluate("Qty * [Unit Price]", ctx));
    }

    [Fact]
    public void Division_by_zero_yields_null_not_exception()
        => Assert.Null(Expr.Evaluate("1 / 0", Ctx()));

    [Fact]
    public void Iif_is_lazy()
        => Assert.Equal("ok", Expr.Evaluate("IIf(1 = 1, 'ok', MissingFn())", Ctx()));

    [Fact]
    public void Unknown_function_throws()
        => Assert.Throws<ExpressionException>(() => Expr.Evaluate("Nope(1)", Ctx()));

    [Fact]
    public void Aggregates_run_over_scope()
    {
        var rows = new[] { 10m, 20m, 30m }
            .Select(v => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Amount"] = v })
            .ToList();
        var ctx = new EvalContext { Row = rows[0], Scope = rows, Culture = Inv };
        Assert.Equal(60m, Expr.Evaluate("Sum(Amount)", ctx));
        Assert.Equal(20m, Expr.Evaluate("Avg(Amount)", ctx));
        Assert.Equal(3m, Expr.Evaluate("Count()", ctx));
        Assert.Equal(30m, Expr.Evaluate("Max(Amount)", ctx));
    }

    [Fact]
    public void Template_mixes_text_and_expressions_and_formats()
    {
        var ctx = Ctx(("Amount", 1234.5m));
        Assert.Equal("Total: 1,234.50", TextTemplate.Parse("Total: {Amount}").EvaluateString(ctx, "N2"));
    }

    [Fact]
    public void Template_escapes_double_braces()
        => Assert.Equal("{x}", TextTemplate.Parse("{{x}}").EvaluateString(Ctx(), null));
}

public class EngineTests
{
    private static ReportResult RunSample(string company = "Test Co")
    {
        var def = SampleReport.Create();
        return ReportEngine.Run(def, new Dictionary<string, string?> { ["Company"] = company },
            new ReportOptions { Culture = CultureInfo.InvariantCulture, Now = new DateTime(2026, 10, 25) });
    }

    [Fact]
    public void Sample_report_paginates_and_numbers_pages()
    {
        var result = RunSample();
        Assert.True(result.Pages.Count >= 1);
        Assert.Empty(result.Warnings);

        var last = result.Pages[^1];
        Assert.Contains(last.Items, i => i.Text == $"Page {result.Pages.Count} of {result.Pages.Count}");
    }

    [Fact]
    public void Parameter_is_substituted()
        => Assert.Contains(RunSample("Zeta Ltd").Pages[0].Items, i => i.Text == "Zeta Ltd");

    [Fact]
    public void Grand_total_equals_sum_of_amounts()
    {
        var rows = ReportStudio.Core.Data.SampleData.Generate();
        decimal expected = rows.Sum(r => (decimal)r["Amount"]!);
        var result = RunSample();
        var grand = result.Pages.SelectMany(p => p.Items)
            .Last(i => i.Band == BandKind.ReportFooter && i.Text.Contains('.'));
        Assert.Equal(expected.ToString("N2", CultureInfo.InvariantCulture), grand.Text);
    }

    [Fact]
    public void Nothing_is_drawn_below_the_page_footer_line()
    {
        var def = SampleReport.Create();
        var result = ReportEngine.Run(def);
        double limit = def.Page.Height - def.Page.MarginBottom - def.PageFooter.Height;
        foreach (var page in result.Pages)
            foreach (var item in page.Items.Where(i => i.Band is BandKind.Detail or BandKind.GroupHeader or BandKind.GroupFooter))
                Assert.True(item.Y + item.Height <= limit + 0.5, $"{item.Text} overflows the body on page {page.Number}");
    }

    [Fact]
    public void Bad_expression_becomes_a_warning_not_a_crash()
    {
        var def = SampleReport.Create();
        def.Detail.Items[0].Text = "{NoSuchField}";
        var result = ReportEngine.Run(def);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Pages[0].Items, i => i.Text == "#ERR");
    }

    [Fact]
    public void Definition_round_trips_through_json()
    {
        var def = SampleReport.Create();
        var copy = ReportDefinition.FromJson(def.ToJson());
        Assert.Equal(def.ToJson(), copy.ToJson());
        Assert.Equal(2, copy.Groups.Count);
    }
}

public class SafeFileTests
{
    private const string Secret = "Server=db;Database=hr;User Id=sa;Password=hunter2;";

    private static ReportDefinition WithSource(DbProvider provider, string query)
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = provider;
        def.DataSource.Query = query;
        def.DataSource.ConnectionString = Secret;
        return def;
    }

    [Fact]
    public void Host_report_never_writes_query_or_connection_string()
    {
        var json = WithSource(DbProvider.Host, "SELECT * FROM salary").ToJson();
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("SELECT", json);
    }

    [Fact]
    public void Host_report_loaded_from_a_file_ignores_query_and_connection_string()
    {
        var json = WithSource(DbProvider.SqlServer, "SELECT 1").ToJson()
            .Replace("\"Provider\": \"SqlServer\"", "\"Provider\": \"Host\"");
        Assert.Contains("\"Host\"", json);

        var loaded = ReportDefinition.FromJson(json);
        Assert.Equal(DbProvider.Host, loaded.DataSource.Provider);
        Assert.Equal("", loaded.DataSource.Query);
        Assert.Equal("", loaded.DataSource.ConnectionString);
    }

    [Theory]
    [InlineData(DbProvider.Sample, "", false)]
    [InlineData(DbProvider.Host, "SELECT 1", false)]
    [InlineData(DbProvider.SqlServer, "SELECT 1", true)]
    [InlineData(DbProvider.PostgreSql, "   ", false)]
    public void HasOwnQuery_only_when_a_database_query_is_present(DbProvider provider, string query, bool expected)
        => Assert.Equal(expected, WithSource(provider, query).HasOwnQuery);

    [Fact]
    public void Masked_connection_string_hides_the_password_only()
    {
        var masked = WithSource(DbProvider.SqlServer, "SELECT 1").DataSource.MaskedConnectionString;
        Assert.DoesNotContain("hunter2", masked);
        Assert.Contains("Server=db", masked);
    }
}

public class IndianFormatTests
{
    private static EvalContext Ctx(params (string K, object? V)[] row)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in row) d[k] = v;
        return new EvalContext
        {
            Row = d, Scope = new[] { (IReadOnlyDictionary<string, object?>)d },
            Culture = ReportStudio.Core.Formatting.IndianFormat.Culture
        };
    }

    [Theory]
    [InlineData(1234567.0, "12,34,567.00")]
    [InlineData(999.5, "999.50")]
    [InlineData(100000.0, "1,00,000.00")]
    [InlineData(123456789.0, "12,34,56,789.00")]
    [InlineData(-1234567.0, "-12,34,567.00")]
    public void Numbers_group_in_lakhs_and_crores(double value, string expected)
        => Assert.Equal(expected, TextTemplate.Parse("{A}").EvaluateString(Ctx(("A", (decimal)value)), "N2"));

    [Fact]
    public void Custom_format_uses_lakh_grouping_too()
        => Assert.Equal("12,34,567.00", TextTemplate.Parse("{A}").EvaluateString(Ctx(("A", 1234567m)), "#,##0.00"));

    [Fact]
    public void Dates_default_to_dd_MM_yyyy_and_keep_the_time_only_when_present()
    {
        Assert.Equal("05-03-2026", TextTemplate.Parse("{D}").EvaluateString(Ctx(("D", new DateTime(2026, 3, 5))), null));
        Assert.Equal("05-03-2026 14:30", TextTemplate.Parse("{D}").EvaluateString(Ctx(("D", new DateTime(2026, 3, 5, 14, 30, 0))), null));
    }

    [Fact]
    public void Money_rounds_halves_away_from_zero()
    {
        Assert.Equal(2.68m, ReportStudio.Core.Formatting.IndianFormat.RoundMoney(2.675m));
        Assert.Equal(-2.68m, ReportStudio.Core.Formatting.IndianFormat.RoundMoney(-2.675m));
        Assert.Equal(3m, Expr.Evaluate("Round(2.5)", Ctx()));
    }

    [Theory]
    [InlineData(0, "Rupees Zero Only")]
    [InlineData(1, "Rupees One Only")]
    [InlineData(21.5, "Rupees Twenty One and Fifty Paise Only")]
    [InlineData(0.05, "Five Paise Only")]
    [InlineData(100000, "Rupees One Lakh Only")]
    [InlineData(123456.5, "Rupees One Lakh Twenty Three Thousand Four Hundred Fifty Six and Fifty Paise Only")]
    [InlineData(12345678, "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight Only")]
    [InlineData(-10, "Minus Rupees Ten Only")]
    public void Amount_in_words_uses_the_indian_system(double amount, string expected)
        => Assert.Equal(expected, ReportStudio.Core.Formatting.IndianFormat.AmountInWords((decimal)amount));

    [Fact]
    public void Amount_in_words_is_available_as_an_expression_function()
        => Assert.Equal("Rupees Ten Only", Expr.Evaluate("AmountInWords(Net)", Ctx(("Net", 10m))));
}

public class FontDefaultTests
{
    [Fact]
    public void New_items_and_rendered_items_default_to_Ubuntu()
    {
        Assert.Equal("Ubuntu", new ReportItem().FontFamily);
        Assert.Equal("Ubuntu", new RenderedItem().FontFamily);
    }

    [Fact]
    public void Sample_report_prints_in_Ubuntu()
        => Assert.All(RunSampleItems(), i => Assert.Equal("Ubuntu", i.FontFamily));

    private static IEnumerable<RenderedItem> RunSampleItems() =>
        ReportEngine.Run(SampleReport.Create()).Pages.SelectMany(p => p.Items).Where(i => i.Kind == ItemKind.Text);
}

public class FieldSchemaTests
{
    [Fact]
    public void Lines_parse_into_names_and_types()
    {
        var fields = FieldDefinition.ParseLines(new[] { "EmpCode", "Basic Pay:Number", " JoinDate : date ", "", "empcode:Text", "Odd:Nope" });

        Assert.Equal(new[] { "EmpCode", "Basic Pay", "JoinDate", "Odd:Nope" }, fields.Select(f => f.Name));
        Assert.Equal(FieldType.Number, fields[1].Type);
        Assert.Equal(FieldType.Date, fields[2].Type);
        Assert.Equal(FieldType.Text, fields[3].Type);   // unknown type text stays part of the name
    }

    [Fact]
    public void Schema_survives_a_save_and_load_and_feeds_the_field_list_without_a_database()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Host;
        def.DataSource.Fields = FieldDefinition.ParseLines(new[] { "EmpCode", "NetPay:Number" });

        var loaded = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal(2, loaded.DataSource.Fields.Count);
        Assert.Equal(FieldType.Number, loaded.DataSource.Fields[1].Type);
        Assert.Equal(new[] { "EmpCode", "NetPay" }, ReportStudio.Core.Data.DataLoader.GetFieldNames(loaded.DataSource));
    }
}

public class DatabaseSplitTests
{
    [Fact]
    public void Database_provider_without_ReportStudio_Data_gives_a_clear_error()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.SqlServer;
        def.DataSource.Query = "SELECT 1";

        // Only Core is referenced by this test project, so no loader has been registered.
        var ex = Assert.Throws<InvalidOperationException>(() => ReportEngine.Run(def));
        Assert.Contains("ReportStudio.Data", ex.Message);
    }

    [Fact]
    public void Core_has_no_database_package_references()
    {
        var names = typeof(ReportDefinition).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");
        Assert.DoesNotContain(names, n => n.Contains("SqlClient") || n.Contains("MySql") || n.Contains("Npgsql"));
    }
}

public class HostRowsTests
{
    private static ReportDefinition HostDefinition()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Host;
        return def;
    }

    private static Dictionary<string, object?> Row(string country, decimal amount) => new()
    {
        ["Country"] = country, ["Customer"] = "A", ["InvoiceNo"] = 1, ["InvoiceDate"] = new DateTime(2026, 1, 1),
        ["Product"] = "X", ["Quantity"] = 2, ["UnitPrice"] = amount / 2, ["Amount"] = amount
    };

    [Fact]
    public void Host_report_uses_the_rows_it_is_given()
    {
        var result = ReportEngine.Run(HostDefinition(), null, new[] { Row("India", 10m), Row("India", 5m) },
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

        Assert.Equal(2, result.RowCount);
        Assert.Contains(result.Pages.SelectMany(p => p.Items), i => i.Text == "India");
        Assert.Contains(result.Pages.SelectMany(p => p.Items), i => i.Band == BandKind.ReportFooter && i.Text == "15.00");
    }

    [Fact]
    public void Host_report_without_rows_is_an_error_not_sample_data()
        => Assert.Throws<InvalidOperationException>(() => ReportEngine.Run(HostDefinition()));

    [Fact]
    public void Host_report_with_no_rows_still_renders_one_page()
    {
        var result = ReportEngine.Run(HostDefinition(), null, Array.Empty<IReadOnlyDictionary<string, object?>>());
        Assert.Single(result.Pages);
    }

    [Fact]
    public void Page_numbers_restart_for_each_group_instance()
    {
        var def = HostDefinition();
        def.Groups[0].RestartPageNumbers = true;

        // "A" is long enough to need several pages; "B" and "C" fit on one page each.
        var rows = Enumerable.Range(0, 120).Select(_ => Row("A", 1m))
            .Concat(new[] { Row("B", 1m), Row("C", 1m) }).ToList();
        var result = ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

        foreach (var section in result.Pages.GroupBy(p => p.Section))
        {
            var pages = section.ToList();
            for (int n = 0; n < pages.Count; n++)
                Assert.Contains(pages[n].Items, i => i.Text == $"Page {n + 1} of {pages.Count}");
        }

        Assert.Equal(3, result.Pages.Select(p => p.Section).Distinct().Count());
        Assert.True(result.Pages.Count(p => p.Section == 1) > 1);
        Assert.Contains(result.Pages[^1].Items, i => i.Text == "Page 1 of 1");
    }

    [Fact]
    public void Without_restart_numbering_runs_across_the_whole_report()
    {
        var rows = Enumerable.Range(0, 120).Select(_ => Row("A", 1m)).Concat(new[] { Row("B", 1m) }).ToList();
        var result = ReportEngine.Run(HostDefinition(), null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

        Assert.All(result.Pages, p => Assert.Equal(1, p.Section));
        Assert.Contains(result.Pages[^1].Items, i => i.Text == $"Page {result.Pages.Count} of {result.Pages.Count}");
    }

    [Fact]
    public void Parameters_are_typed_for_the_host()
    {
        var def = HostDefinition();
        def.Parameters.Add(new ReportParameter { Name = "Year", Type = ParameterType.Number, DefaultValue = "2026" });
        var typed = ReportEngine.ResolveParameters(def, new Dictionary<string, string?> { ["Year"] = "2025" });
        Assert.Equal(2025m, typed["Year"]);
    }
}
