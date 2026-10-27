using System.Globalization;
using System.Text;
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

public class BarcodeTests
{
    private static string Digits(params string[] patterns) => string.Concat(patterns);
    private static string Digits(int[] runs) => string.Concat(runs);

    [Fact]
    public void Code128_set_B_encodes_start_data_checksum_and_stop()
    {
        // 'A' = value 33, start B = 104, checksum (104 + 33 * 1) % 103 = 34
        Assert.True(ReportStudio.Core.Barcodes.BarcodeEncoder.TryEncode(
            ReportStudio.Core.Barcodes.BarcodeType.Code128, "A", out var runs, out _));
        Assert.Equal(Digits("211214", "111323", "131123", "2331112"), Digits(runs));
    }

    [Fact]
    public void Code128_uses_set_C_for_even_length_digits()
    {
        // start C = 105, pairs 12 and 34, checksum (105 + 12 * 1 + 34 * 2) % 103 = 82
        Assert.True(ReportStudio.Core.Barcodes.BarcodeEncoder.TryEncode(
            ReportStudio.Core.Barcodes.BarcodeType.Code128, "1234", out var runs, out _));
        Assert.Equal(Digits("211232", "112232", "131123", "121241", "2331112"), Digits(runs));
    }

    [Theory]
    [InlineData("Hello 123", 11 * 11 + 13)]   // start + 9 chars + checksum = 11 symbols of 11 modules, then the 13-module stop
    public void Code128_symbol_widths_add_up(string text, int expectedModules)
    {
        Assert.True(ReportStudio.Core.Barcodes.BarcodeEncoder.TryEncode(
            ReportStudio.Core.Barcodes.BarcodeType.Code128, text, out var runs, out _));
        Assert.Equal(expectedModules, runs.Sum());
    }

    [Fact]
    public void Code39_frames_the_value_with_stars_and_separates_characters()
    {
        Assert.True(ReportStudio.Core.Barcodes.BarcodeEncoder.TryEncode(
            ReportStudio.Core.Barcodes.BarcodeType.Code39, "a", out var runs, out _));   // lower case is upper-cased
        // 3 characters of 15 modules (6 narrow + 3 wide of 3) and 2 gaps of 1
        Assert.Equal(3 * 15 + 2, runs.Sum());
        Assert.Equal(3 * 9 + 2, runs.Length);
    }

    [Theory]
    [InlineData(ReportStudio.Core.Barcodes.BarcodeType.Code128, "")]
    [InlineData(ReportStudio.Core.Barcodes.BarcodeType.Code128, "café")]
    [InlineData(ReportStudio.Core.Barcodes.BarcodeType.Code39, "A*B")]
    [InlineData(ReportStudio.Core.Barcodes.BarcodeType.Code39, "a_b")]
    public void Unwritable_values_are_rejected_with_a_reason(ReportStudio.Core.Barcodes.BarcodeType type, string text)
    {
        Assert.False(ReportStudio.Core.Barcodes.BarcodeEncoder.TryEncode(type, text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Bars_fit_inside_the_box_with_quiet_zones()
    {
        var bars = ReportStudio.Core.Barcodes.BarcodeLayout.Bars(ReportStudio.Core.Barcodes.BarcodeType.Code128, "ABC", 200);
        Assert.NotEmpty(bars);
        Assert.True(bars[0].X > 0);
        Assert.True(bars[^1].X + bars[^1].Width < 200);
    }

    private static ReportDefinition WithBarcode(string text, bool showText = true)
    {
        var def = SampleReport.Create();
        def.PageHeader.Items.Add(new ReportItem
        {
            Name = "Code", Kind = ItemKind.Barcode, X = 0, Y = 0, Width = 180, Height = 40, Text = text,
            ShowText = showText, FontSize = 8
        });
        return def;
    }

    [Fact]
    public void Engine_prints_bars_and_the_value_underneath()
    {
        var result = ReportEngine.Run(WithBarcode("INV-1001"));
        var page = result.Pages[0];
        var bars = Assert.Single(page.Items, i => i.Kind == ItemKind.Barcode);
        var caption = Assert.Single(page.Items, i => i.Kind == ItemKind.Text && i.Text == "INV-1001");

        Assert.Equal("INV-1001", bars.Text);
        Assert.True(bars.Height < 40);
        Assert.Equal(bars.Y + bars.Height, caption.Y, 3);
        Assert.Equal(40, bars.Height + caption.Height, 3);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Barcode_without_text_has_no_caption()
    {
        var page = ReportEngine.Run(WithBarcode("INV-1001", showText: false)).Pages[0];
        Assert.DoesNotContain(page.Items, i => i.Kind == ItemKind.Text && i.Text == "INV-1001");
        Assert.Equal(40, Assert.Single(page.Items, i => i.Kind == ItemKind.Barcode).Height);
    }

    [Fact]
    public void Unwritable_barcode_value_warns_and_prints_nothing()
    {
        var def = WithBarcode("café");
        var result = ReportEngine.Run(def);

        Assert.Contains(result.Warnings, w => w.Contains("Barcode 'Code'"));
        Assert.Equal("", Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode).Text);
    }

    [Fact]
    public void Barcode_settings_survive_save_and_load()
    {
        var def = WithBarcode("X1");
        def.PageHeader.Items[^1].BarcodeType = ReportStudio.Core.Barcodes.BarcodeType.Code39;
        def.PageHeader.Items[^1].ShowText = false;

        var item = ReportDefinition.FromJson(def.ToJson()).PageHeader.Items[^1];
        Assert.Equal(ItemKind.Barcode, item.Kind);
        Assert.Equal(ReportStudio.Core.Barcodes.BarcodeType.Code39, item.BarcodeType);
        Assert.False(item.ShowText);
    }
}

public class QrTests
{
    private static string Sha(bool[][] modules)
    {
        var rows = string.Join("\n", modules.Select(r => new string(r.Select(c => c ? '1' : '0').ToArray())));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rows))).ToLowerInvariant();
    }

    // Vectors made by a reference implementation whose output was decoded back to the text with OpenCV
    // for every version (1-40) and level.
    [Theory]
    [InlineData("HELLO WORLD", ReportStudio.Core.Barcodes.QrLevel.Low, 21, "fcf618c7c5eff92bae86e504a81a125d4a53264770b891c0ac7c3e3da17c73c6")]
    [InlineData("https://example.com/pay?id=12345&amt=100.00", ReportStudio.Core.Barcodes.QrLevel.Medium, 33, "b49992ad2802273654eb5d58a9f7aece15420a3943bbd92a235dd9629c0e05a1")]
    [InlineData("1", ReportStudio.Core.Barcodes.QrLevel.Low, 21, "ba40868d32ba180068960cb38915c5e49d013a7f3a8bed731c58f0a797247dd6")]
    public void Small_codes_match_the_reference(string text, ReportStudio.Core.Barcodes.QrLevel level, int size, string hash)
    {
        Assert.True(ReportStudio.Core.Barcodes.QrEncoder.TryEncode(text, level, out var modules, out _));
        Assert.Equal(size, modules.Length);
        Assert.Equal(hash, Sha(modules));
    }

    [Fact]
    public void Utf8_text_with_version_information_matches_the_reference()
    {
        var text = "Payslip ₹ 8,10,346.25 " + new string('x', 140);     // version 11 has version-information blocks
        Assert.True(ReportStudio.Core.Barcodes.QrEncoder.TryEncode(text, ReportStudio.Core.Barcodes.QrLevel.Quartile, out var modules, out _));
        Assert.Equal(61, modules.Length);
        Assert.Equal("46fda16a272bd195a01627d28902d70a6ef6aadd614c6b4a7147b20870cd32e4", Sha(modules));
    }

    [Fact]
    public void Large_multi_block_code_matches_the_reference()
    {
        Assert.True(ReportStudio.Core.Barcodes.QrEncoder.TryEncode(new string('A', 600), ReportStudio.Core.Barcodes.QrLevel.High, out var modules, out _));
        Assert.Equal(125, modules.Length);     // version 27
        Assert.Equal("8bb5bc11d4aeea94348e61cbeed43c2e7f06d8c5926ae3b6ca79d18400487630", Sha(modules));
    }

    [Fact]
    public void Empty_and_too_long_values_are_rejected_with_a_reason()
    {
        Assert.False(ReportStudio.Core.Barcodes.QrEncoder.TryEncode("", ReportStudio.Core.Barcodes.QrLevel.Low, out _, out var e1));
        Assert.NotEmpty(e1);
        Assert.False(ReportStudio.Core.Barcodes.QrEncoder.TryEncode(new string('A', 3000), ReportStudio.Core.Barcodes.QrLevel.Low, out _, out var e2));
        Assert.Contains("too long", e2);
        Assert.True(ReportStudio.Core.Barcodes.QrEncoder.TryEncode(new string('A', 2953), ReportStudio.Core.Barcodes.QrLevel.Low, out var max, out _));
        Assert.Equal(177, max.Length);         // version 40 holds 2953 bytes at level L
    }

    [Fact]
    public void Layout_is_a_centred_square_inside_the_box()
    {
        var cells = ReportStudio.Core.Barcodes.QrLayout.Rectangles("hello", ReportStudio.Core.Barcodes.QrLevel.Medium, 200, 100);
        Assert.NotEmpty(cells);
        Assert.All(cells, c =>
        {
            Assert.InRange(c.X, 50 - 0.001, 150 + 0.001);   // a 100 x 100 square centred in the 200 wide box
            Assert.InRange(c.Y, 0, 100);
            Assert.True(c.X + c.Width <= 150 + 0.001);
        });
    }

    private static ReportDefinition WithQr(string text, ReportStudio.Core.Barcodes.QrLevel level = ReportStudio.Core.Barcodes.QrLevel.Medium)
    {
        var def = SampleReport.Create();
        def.PageHeader.Items.Add(new ReportItem { Name = "Qr", Kind = ItemKind.QrCode, X = 0, Y = 0, Width = 80, Height = 80, Text = text, QrLevel = level });
        return def;
    }

    [Fact]
    public void Engine_prints_the_qr_value_and_level()
    {
        var result = ReportEngine.Run(WithQr("Pay {@Company}", ReportStudio.Core.Barcodes.QrLevel.High),
            new Dictionary<string, string?> { ["Company"] = "Zeta" });

        var qr = Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.QrCode);
        Assert.Equal("Pay Zeta", qr.Text);
        Assert.Equal(ReportStudio.Core.Barcodes.QrLevel.High, qr.QrLevel);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Qr_value_that_cannot_be_encoded_warns_and_prints_nothing()
    {
        var result = ReportEngine.Run(WithQr(new string('A', 3500), ReportStudio.Core.Barcodes.QrLevel.Low));

        Assert.Contains(result.Warnings, w => w.Contains("QR code 'Qr'"));
        Assert.Equal("", Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.QrCode).Text);
    }

    [Fact]
    public void Qr_level_survives_save_and_load()
    {
        var item = ReportDefinition.FromJson(WithQr("x", ReportStudio.Core.Barcodes.QrLevel.Quartile).ToJson()).PageHeader.Items[^1];
        Assert.Equal(ItemKind.QrCode, item.Kind);
        Assert.Equal(ReportStudio.Core.Barcodes.QrLevel.Quartile, item.QrLevel);
    }
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

public class CrossTabTests
{
    private static ReportDefinition WithCrossTab(ReportItem tab)
    {
        var def = new ReportDefinition { Name = "Tab" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 0;
        tab.Kind = ItemKind.CrossTab; tab.X = 0; tab.Y = 0; tab.Width = 400; tab.Height = 20;
        def.ReportFooter.Height = 20;
        def.ReportFooter.Items.Add(tab);
        return def;
    }

    private static Dictionary<string, object?> Row(string dept, string month, decimal net) =>
        new() { ["Dept"] = dept, ["Month"] = month, ["Net"] = net };

    private static string[][] Grid(ReportResult result) =>
        result.Pages[0].Items.GroupBy(i => i.Y).OrderBy(g => g.Key)
            .Select(g => g.OrderBy(i => i.X).Select(i => i.Text).ToArray()).ToArray();

    private static readonly ReportOptions Invariant = new() { Culture = CultureInfo.InvariantCulture };

    [Fact]
    public void Sums_rows_by_column_with_totals()
    {
        var def = WithCrossTab(new ReportItem { CrossRows = "Dept", CrossColumns = "Month", CrossValue = "Net" });
        var rows = new[] { Row("A", "Jan", 10m), Row("A", "Feb", 5m), Row("B", "Jan", 7m) };

        var result = ReportEngine.Run(def, null, rows, Invariant);

        Assert.Empty(result.Warnings);
        Assert.Equal(new[]
        {
            new[] { "", "Feb", "Jan", "Total" },
            new[] { "A", "5.00", "10.00", "15.00" },
            new[] { "B", "", "7.00", "7.00" },
            new[] { "Total", "5.00", "17.00", "22.00" }
        }, Grid(result));
    }

    [Fact]
    public void Count_needs_no_value_and_totals_can_be_switched_off()
    {
        var def = WithCrossTab(new ReportItem
        {
            CrossRows = "Dept", CrossColumns = "Month", CrossAggregate = CrossAggregate.Count, CrossTotals = false
        });
        var rows = new[] { Row("A", "Jan", 10m), Row("A", "Jan", 5m), Row("B", "Jan", 7m) };

        var grid = Grid(ReportEngine.Run(def, null, rows, Invariant));

        Assert.Equal(new[]
        {
            new[] { "", "Jan" },
            new[] { "A", "2" },
            new[] { "B", "1" }
        }, grid);
    }

    [Fact]
    public void The_band_grows_to_hold_the_grid()
    {
        var def = WithCrossTab(new ReportItem { CrossRows = "Dept", CrossColumns = "Month", CrossValue = "Net" });
        var rows = new[] { Row("A", "Jan", 1m), Row("B", "Jan", 1m) };

        var result = ReportEngine.Run(def, null, rows, Invariant);

        // header + 2 rows + total row = 4 lines of 20 px: the last line must start at 60 px from the page top margin.
        var top = result.Pages[0].Items.Min(i => i.Y);
        Assert.Equal(60, result.Pages[0].Items.Max(i => i.Y) - top, 3);
    }

    [Fact]
    public void Missing_settings_warn_and_print_nothing()
    {
        var def = WithCrossTab(new ReportItem { CrossRows = "Dept" });
        var result = ReportEngine.Run(def, null, new[] { Row("A", "Jan", 1m) }, Invariant);

        Assert.Single(result.Warnings);
        Assert.Empty(result.Pages[0].Items);
    }
}

public class SubReportTests
{
    private static readonly ReportOptions Invariant = new() { Culture = CultureInfo.InvariantCulture };

    private static ReportDefinition Bare(string name)
    {
        var def = new ReportDefinition { Name = name };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        return def;
    }

    private static ReportItem TextItem(string text, double y) =>
        new() { Kind = ItemKind.Text, Text = text, X = 0, Y = y, Width = 200, Height = 20 };

    /// <summary>Parent: one name line, then a sub-report. Child: one line per row.</summary>
    private static (ReportDefinition parent, ReportDefinition child) Pair(ReportItem sub)
    {
        var parent = Bare("Parent");
        parent.Detail.Height = 22;
        parent.Detail.Items.Add(TextItem("{Name}", 0));
        sub.Kind = ItemKind.SubReport; sub.Y = 22; sub.Width = 200; sub.Height = 10;
        parent.Detail.Items.Add(sub);

        var child = Bare("Child");
        child.Detail.Height = 20;
        child.Detail.Items.Add(TextItem("{Item}", 0));
        return (parent, child);
    }

    private static Dictionary<string, object?> ChildRow(string item, string? empCode = null)
    {
        var row = new Dictionary<string, object?> { ["Item"] = item };
        if (empCode != null) row["EmpCode"] = empCode;
        return row;
    }

    [Fact]
    public void Rows_held_in_the_parent_row_fill_the_sub_report_and_grow_the_band()
    {
        var (parent, child) = Pair(new ReportItem { SubReportPath = "lines", SubReportData = "Lines" });
        var options = new ReportOptions
        {
            Culture = CultureInfo.InvariantCulture,
            SubReports = new Dictionary<string, ReportDefinition> { ["lines"] = child }
        };
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Asha", ["Lines"] = new[] { ChildRow("Basic"), ChildRow("HRA") } },
            new Dictionary<string, object?> { ["Name"] = "Ravi", ["Lines"] = new[] { ChildRow("Basic") } }
        };

        var result = ReportEngine.Run(parent, null, rows, options);
        var items = result.Pages[0].Items;

        Assert.Empty(result.Warnings);
        Assert.Equal(2, items.Count(i => i.Text == "Basic"));
        Assert.Single(items, i => i.Text == "HRA");

        // Asha's band is 22 + 2 child lines (40) tall, so Ravi starts below that.
        var asha = items.Single(i => i.Text == "Asha");
        var ravi = items.Single(i => i.Text == "Ravi");
        Assert.Equal(62, ravi.Y - asha.Y, 3);
    }

    [Fact]
    public void A_data_set_is_matched_to_each_parent_row_by_the_link_fields()
    {
        var (parent, child) = Pair(new ReportItem
        {
            SubReportPath = "lines", SubReportData = "Lines", LinkChild = "EmpCode", LinkParent = "Code"
        });
        var options = new ReportOptions
        {
            Culture = CultureInfo.InvariantCulture,
            SubReports = new Dictionary<string, ReportDefinition> { ["lines"] = child },
            DataSets = new Dictionary<string, List<Dictionary<string, object?>>>
            {
                ["Lines"] = new()
                {
                    ChildRow("Basic-1", "E1"), ChildRow("HRA-1", "E1"), ChildRow("Basic-2", "E2")
                }
            }
        };
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Asha", ["Code"] = "E1" },
            new Dictionary<string, object?> { ["Name"] = "Ravi", ["Code"] = "E2" }
        };

        var items = ReportEngine.Run(parent, null, rows, options).Pages[0].Items;

        Assert.Equal(new[] { "Basic-1", "HRA-1", "Basic-2" },
            items.Where(i => i.Text.Contains('-')).OrderBy(i => i.Y).Select(i => i.Text).ToArray());
    }

    [Fact]
    public void Parameters_are_passed_from_the_parent_row()
    {
        var (parent, child) = Pair(new ReportItem
        {
            SubReportPath = "lines", SubReportData = "Lines", SubReportParameters = "Who={Name}"
        });
        child.Parameters.Add(new ReportParameter { Name = "Who", Type = ParameterType.Text });
        child.Detail.Items[0].Text = "{@Who}: {Item}";
        var options = new ReportOptions
        {
            Culture = CultureInfo.InvariantCulture,
            SubReports = new Dictionary<string, ReportDefinition> { ["lines"] = child }
        };
        var rows = new[] { new Dictionary<string, object?> { ["Name"] = "Asha", ["Lines"] = new[] { ChildRow("Basic") } } };

        var items = ReportEngine.Run(parent, null, rows, options).Pages[0].Items;
        Assert.Contains(items, i => i.Text == "Asha: Basic");
    }

    [Fact]
    public void A_file_outside_the_report_folder_is_refused_with_a_warning()
    {
        var (parent, _) = Pair(new ReportItem { SubReportPath = "../secret.rsr", SubReportData = "Lines" });
        var dir = Path.Combine(Path.GetTempPath(), "rs-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new ReportOptions { Culture = CultureInfo.InvariantCulture, BaseDirectory = dir };
            var rows = new[] { new Dictionary<string, object?> { ["Name"] = "Asha" } };

            var result = ReportEngine.Run(parent, null, rows, options);

            Assert.Contains(result.Warnings, w => w.Contains("inside the report's folder"));
            Assert.Contains(result.Pages[0].Items, i => i.Text == "Asha");   // the parent still prints
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unknown_sub_report_warns_instead_of_failing()
    {
        var (parent, _) = Pair(new ReportItem { SubReportPath = "nope", SubReportData = "Lines" });
        var rows = new[] { new Dictionary<string, object?> { ["Name"] = "Asha" } };

        var result = ReportEngine.Run(parent, null, rows, Invariant);

        Assert.Single(result.Warnings);
        Assert.Contains(result.Pages[0].Items, i => i.Text == "Asha");
    }

    [Fact]
    public void A_sub_report_that_contains_itself_stops_at_the_depth_limit()
    {
        var (parent, child) = Pair(new ReportItem { SubReportPath = "loop", SubReportData = "Lines" });
        child.Detail.Items.Add(new ReportItem
        {
            Kind = ItemKind.SubReport, SubReportPath = "loop", SubReportData = "Lines", Y = 20, Width = 100, Height = 10
        });
        var options = new ReportOptions
        {
            Culture = CultureInfo.InvariantCulture,
            SubReports = new Dictionary<string, ReportDefinition> { ["loop"] = child }
        };
        var loopRow = ChildRow("x");
        loopRow["Lines"] = new[] { loopRow };   // every level has rows again, so only the limit can stop it
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Asha", ["Lines"] = new[] { loopRow } }
        };

        var result = ReportEngine.Run(parent, null, rows, options);

        Assert.Contains(result.Warnings, w => w.Contains("nested only"));
    }
}
