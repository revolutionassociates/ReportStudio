using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReportStudio.Core.Data;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Direct file sources (requirement 40, first part): CSV, JSON, XML and Excel files read as report data.</summary>
public sealed class FileSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-sources-" + Guid.NewGuid().ToString("N"));

    public FileSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private static MemoryStream Text(string text) => new(new UTF8Encoding(false).GetBytes(text));

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    // ---- CSV ----

    [Fact]
    public void Csv_reads_a_header_and_recognises_numbers_dates_and_codes()
    {
        var rows = CsvSource.Read(Text("Name,Amount,Code,When,Active\nAsha,1200.50,007,2026-11-09,true\n"), ",", true);

        var row = Assert.Single(rows);
        Assert.Equal("Asha", row["Name"]);
        Assert.Equal(1200.5m, row["Amount"]);
        Assert.Equal("007", row["Code"]);                                  // a leading zero makes it a code, not a number
        Assert.Equal(new DateTime(2026, 11, 9), row["When"]);
        Assert.Equal(true, row["Active"]);
    }

    [Fact]
    public void Csv_keeps_very_long_digit_strings_as_text()
    {
        var row = Assert.Single(CsvSource.Read(Text("Card\n1234567890123456\n"), ",", true));

        Assert.Equal("1234567890123456", row["Card"]);
    }

    [Fact]
    public void Csv_reads_quoted_values_with_separators_quotes_and_line_breaks()
    {
        var rows = CsvSource.Read(Text("Name,Note\r\n\"Doe, John\",\"said \"\"hi\"\"\"\r\n\"Roy\",\"line1\nline2\"\r\n"), ",", true);

        Assert.Equal("Doe, John", rows[0]["Name"]);
        Assert.Equal("said \"hi\"", rows[0]["Note"]);
        Assert.Equal("line1\nline2", rows[1]["Note"]);
    }

    [Theory]
    [InlineData("A;B\n1;2\n", null)]
    [InlineData("A\tB\n1\t2\n", null)]
    [InlineData("A|B\n1|2\n", null)]
    [InlineData("A\tB\n1\t2\n", "\\t")]
    [InlineData("A\tB\n1\t2\n", "tab")]
    [InlineData("A:B\n1:2\n", ":")]
    public void Csv_works_out_or_takes_the_separator(string text, string? delimiter)
    {
        var row = Assert.Single(CsvSource.Read(Text(text), delimiter, true));

        Assert.Equal(1m, row["A"]);
        Assert.Equal(2m, row["B"]);
    }

    [Fact]
    public void Csv_without_a_header_names_the_fields_Column1_Column2()
    {
        var rows = CsvSource.Read(Text("a,b\nc,d\n"), ",", hasHeader: false);

        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0]["Column1"]);
        Assert.Equal("d", rows[1]["Column2"]);
    }

    [Fact]
    public void Csv_repairs_empty_and_repeated_header_names()
    {
        var row = Assert.Single(CsvSource.Read(Text("A,A,,\n1,2,3,4\n"), ",", true));

        Assert.Equal(new[] { "A", "A_2", "Column3", "Column4" }, row.Keys.ToArray());
    }

    [Fact]
    public void Csv_pads_short_rows_skips_blank_lines_and_reads_a_byte_order_mark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("A,B\n\n1\n")).ToArray();

        var row = Assert.Single(CsvSource.Read(new MemoryStream(bytes), ",", true));

        Assert.Equal(1m, row["A"]);
        Assert.Null(row["B"]);
    }

    [Fact]
    public void Csv_refuses_more_rows_than_allowed_unless_only_a_sample_is_wanted()
    {
        var text = "A\n1\n2\n3\n4\n5\n";

        Assert.Throws<SourceException>(() => CsvSource.Read(Text(text), ",", true, maxRows: 3));
        Assert.Equal(3, CsvSource.Read(Text(text), ",", true, maxRows: 3, stopAtLimit: true).Count);
    }

    // ---- JSON ----

    [Fact]
    public void Json_reads_an_array_of_objects_and_flattens_what_is_nested()
    {
        var rows = JsonSource.Read(Text(@"[
            {""name"":""Asha"",""age"":31,""active"":true,""boss"":null,""address"":{""city"":""Pune"",""geo"":{""lat"":18.5}},""tags"":[""a"",""b""]},
            {""name"":""Ravi""}]"), null);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Asha", rows[0]["name"]);
        Assert.Equal(31m, rows[0]["age"]);
        Assert.Equal(true, rows[0]["active"]);
        Assert.Null(rows[0]["boss"]);
        Assert.Equal("Pune", rows[0]["address.city"]);
        Assert.Equal(18.5m, rows[0]["address.geo.lat"]);
        Assert.Equal("a, b", rows[0]["tags"]);
    }

    [Fact]
    public void Json_text_stays_text_except_a_date_and_a_value_array_becomes_Value()
    {
        var row = Assert.Single(JsonSource.Read(Text(@"[{""n"":""5"",""d"":""2026-11-09""}]"), null));
        Assert.Equal("5", row["n"]);
        Assert.Equal(new DateTime(2026, 11, 9), row["d"]);

        var plain = JsonSource.Read(Text("[1, 2, 3]"), null);
        Assert.Equal(new object?[] { 1m, 2m, 3m }, plain.Select(r => r["Value"]).ToArray());
    }

    [Theory]
    [InlineData("data.items")]
    [InlineData("$.data.items")]
    [InlineData("DATA.Items")]
    public void Json_finds_the_rows_by_a_path(string selector)
    {
        var rows = JsonSource.Read(Text(@"{""data"":{""items"":[{""id"":1},{""id"":2}]}}"), selector);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1m, rows[0]["id"]);
    }

    [Fact]
    public void Json_follows_index_steps_and_a_single_object_is_one_row()
    {
        var json = @"{""orders"":[{""lines"":[{""q"":1}]},{""lines"":[{""q"":7},{""q"":8}]}]}";

        var rows = JsonSource.Read(Text(json), "$.orders[1].lines");

        Assert.Equal(new object?[] { 7m, 8m }, rows.Select(r => r["q"]).ToArray());
        Assert.Single(JsonSource.Read(Text(@"{""a"":1}"), null));
    }

    [Fact]
    public void Json_that_leads_nowhere_or_is_not_json_or_is_nested_far_too_deep_is_refused()
    {
        Assert.Throws<SourceException>(() => JsonSource.Read(Text(@"{""a"":[1]}"), "nothing.here"));
        Assert.Throws<SourceException>(() => JsonSource.Read(Text("{not json"), null));
        Assert.Throws<SourceException>(() => JsonSource.Read(Text(new string('[', 200) + new string(']', 200)), null));
    }

    // ---- XML ----

    private const string Orders = @"<orders>
        <order id=""1""><customer>Asha</customer><tag>a</tag><tag>b</tag><address><city>Pune</city></address><total>12.5</total></order>
        <order id=""2""><customer>Ravi</customer></order></orders>";

    [Theory]
    [InlineData("")]
    [InlineData("orders/order")]
    [InlineData("//order")]
    public void Xml_finds_the_rows_by_element_names(string selector)
    {
        var rows = XmlSource.Read(Text(Orders), selector);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1m, rows[0]["id"]);
        Assert.Equal("Asha", rows[0]["customer"]);
        Assert.Equal("a, b", rows[0]["tag"]);
        Assert.Equal("Pune", rows[0]["address.city"]);
        Assert.Equal(12.5m, rows[0]["total"]);
        Assert.Equal("Ravi", rows[1]["customer"]);
    }

    [Fact]
    public void Xml_refuses_a_DTD_so_entities_and_outside_files_are_never_read()
    {
        const string bomb = @"<!DOCTYPE x [<!ENTITY a ""boom"">]><x><r>&a;</r></x>";

        Assert.Throws<SourceException>(() => XmlSource.Read(Text(bomb), ""));
        Assert.Throws<SourceException>(() => XmlSource.Read(Text("<a><b></a>"), ""));
    }

    // ---- Excel ----

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static MemoryStream Workbook(string sheet1)
    {
        var serial = new DateTime(2026, 11, 9).ToOADate().ToString(CultureInfo.InvariantCulture);
        var next = new DateTime(2026, 11, 10).ToOADate().ToString(CultureInfo.InvariantCulture);
        sheet1 = sheet1.Replace("{D1}", serial).Replace("{D2}", next);

        var parts = new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $@"<workbook xmlns=""{Main}"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""><sheets>
                <sheet name=""People"" sheetId=""1"" r:id=""rId1""/><sheet name=""Other"" sheetId=""2"" r:id=""rId2""/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
                <Relationship Id=""rId1"" Target=""worksheets/sheet1.xml""/><Relationship Id=""rId2"" Target=""worksheets/sheet2.xml""/></Relationships>",
            ["xl/sharedStrings.xml"] = $@"<sst xmlns=""{Main}""><si><t>Name</t></si><si><t>Amount</t></si><si><t>Joined</t></si><si><t>Asha</t></si>
                <si><t>Ravi</t></si><si><t>Active</t></si></sst>",
            ["xl/styles.xml"] = $@"<styleSheet xmlns=""{Main}""><numFmts count=""1""><numFmt numFmtId=""164"" formatCode=""dd-mmm-yyyy""/></numFmts>
                <cellXfs count=""3""><xf numFmtId=""0""/><xf numFmtId=""14""/><xf numFmtId=""164""/></cellXfs></styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $@"<worksheet xmlns=""{Main}"">{sheet1}</worksheet>",
            ["xl/worksheets/sheet2.xml"] = $@"<worksheet xmlns=""{Main}""><sheetData/></worksheet>",
        };
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    private const string People = @"<sheetData>
        <row r=""1""><c r=""A1"" t=""s""><v>0</v></c><c r=""B1"" t=""s""><v>1</v></c><c r=""C1"" t=""s""><v>2</v></c><c r=""D1"" t=""s""><v>5</v></c></row>
        <row r=""2""><c r=""A2"" t=""s""><v>3</v></c><c r=""B2""><v>1200.5</v></c><c r=""C2"" s=""1""><v>{D1}</v></c></row>
        <row r=""3""><c r=""A3"" t=""s""><v>4</v></c><c r=""C3"" s=""2""><v>{D2}</v></c></row>
        <row r=""4""/>
        <row r=""5""><c r=""A5"" t=""inlineStr""><is><t>Inline</t></is></c><c r=""D5"" t=""b""><v>1</v></c></row></sheetData>";

    [Fact]
    public void Excel_reads_text_numbers_dates_flags_and_blank_cells()
    {
        var rows = ExcelSource.Read(Workbook(People), null, hasHeader: true);

        Assert.Equal(3, rows.Count);                                       // the blank row 4 is skipped
        Assert.Equal("Asha", rows[0]["Name"]);
        Assert.Equal(1200.5m, rows[0]["Amount"]);
        Assert.Equal(new DateTime(2026, 11, 9), rows[0]["Joined"]);       // a built-in date format
        Assert.Null(rows[1]["Amount"]);                                    // a cell that is not there
        Assert.Equal(new DateTime(2026, 11, 10), rows[1]["Joined"]);      // a custom date format
        Assert.Equal("Inline", rows[2]["Name"]);
        Assert.Equal(true, rows[2]["Active"]);
    }

    [Fact]
    public void Excel_picks_a_sheet_by_name_and_names_the_sheets_when_it_is_not_there()
    {
        Assert.Empty(ExcelSource.Read(Workbook(People), "other", true));

        var ex = Assert.Throws<SourceException>(() => ExcelSource.Read(Workbook(People), "Nope", true));
        Assert.Contains("People", ex.Message);
        Assert.Contains("Other", ex.Message);
    }

    [Fact]
    public void Excel_refuses_a_file_that_is_not_a_workbook()
    {
        Assert.Throws<SourceException>(() => ExcelSource.Read(new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip")), null, true));
    }

    // ---- paths and the loader ----

    [Fact]
    public void A_relative_file_name_is_looked_up_in_the_base_folder()
    {
        Write("data/people.csv", "A\n1\n");

        var path = FileSourceLoader.ResolvePath("data/people.csv", new SourceSettings { BaseDirectory = _dir });

        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "data", "people.csv")), Path.GetFullPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/x.csv")]
    [InlineData(@"\\server\share\x.csv")]
    [InlineData("missing.csv")]
    public void A_file_name_that_may_not_or_cannot_be_read_is_refused_with_a_message(string source)
    {
        var ex = Assert.Throws<SourceException>(() => FileSourceLoader.ResolvePath(source, new SourceSettings { BaseDirectory = _dir }));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Only_files_inside_the_allowed_folder_can_be_read()
    {
        Write("inside/a.csv", "A\n1\n");
        Write("outside/b.csv", "A\n1\n");
        var settings = new SourceSettings { BaseDirectory = Path.Combine(_dir, "inside"), FileRoot = Path.Combine(_dir, "inside") };

        Assert.EndsWith("a.csv", FileSourceLoader.ResolvePath("a.csv", settings));
        Assert.Throws<SourceException>(() => FileSourceLoader.ResolvePath(Path.Combine("..", "outside", "b.csv"), settings));
        Assert.Throws<SourceException>(() => FileSourceLoader.ResolvePath(Path.Combine(_dir, "outside", "b.csv"), settings));
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_the_allowed_one_is_outside_it()
    {
        Write("data/a.csv", "A\n1\n");
        Write("data-secret/b.csv", "A\n1\n");
        var settings = new SourceSettings { BaseDirectory = Path.Combine(_dir, "data"), FileRoot = Path.Combine(_dir, "data") };

        Assert.Throws<SourceException>(() => FileSourceLoader.ResolvePath(Path.Combine("..", "data-secret", "b.csv"), settings));
    }

    [Fact]
    public void The_loader_reads_each_kind_of_file_and_lists_the_field_names()
    {
        Write("a.csv", "Name,Qty\nAsha,2\n");
        Write("a.json", @"{""rows"":[{""Name"":""Asha"",""Qty"":2}]}");
        Write("a.xml", "<r><row><Name>Asha</Name><Qty>2</Qty></row></r>");
        var settings = new SourceSettings { BaseDirectory = _dir };
        var parameters = new Dictionary<string, object?>();

        var csv = new DataSourceDefinition { Provider = DbProvider.Csv, Source = "a.csv" };
        var json = new DataSourceDefinition { Provider = DbProvider.Json, Source = "a.json", Selector = "rows" };
        var xml = new DataSourceDefinition { Provider = DbProvider.Xml, Source = "a.xml" };

        foreach (var ds in new[] { csv, json, xml })
        {
            var row = Assert.Single(DataLoader.Load(ds, parameters, settings));
            Assert.Equal("Asha", row["Name"]);
            Assert.Equal(2m, row["Qty"]);
            Assert.Equal(new[] { "Name", "Qty" }, DataLoader.GetFieldNames(ds, settings));
        }
    }

    [Fact]
    public void Testing_a_file_that_is_not_there_says_so()
    {
        var ds = new DataSourceDefinition { Provider = DbProvider.Csv, Source = "nope.csv" };

        Assert.ThrowsAny<Exception>(() => DataLoader.TestConnection(ds, new SourceSettings { BaseDirectory = _dir }));
    }

    // ---- in a report ----

    private static ReportDefinition CsvReport(string source)
    {
        var def = new ReportDefinition { Name = "File" };
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = source;
        return def;
    }

    [Fact]
    public void A_report_that_reads_a_file_it_may_not_stops_with_a_message()
    {
        var def = CsvReport(@"\\server\share\people.csv");

        Assert.Throws<SourceException>(() => ReportEngine.Run(def, null, new ReportOptions { BaseDirectory = _dir }));
    }

    // ---- the definition ----

    [Fact]
    public void A_file_source_with_a_source_needs_trust_and_one_without_does_not()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.Csv;
        Assert.False(def.HasOwnQuery);

        def.DataSource.Source = "people.csv";
        Assert.True(def.HasOwnQuery);
    }

    [Theory]
    [InlineData("https://user:pass@example.com/api", "pass")]
    [InlineData("https://example.com/api?apikey=abc123&page=2", "abc123")]
    [InlineData("https://example.com/api?access_token=abc123&x=1", "abc123")]
    public void A_secret_in_a_source_is_hidden_when_it_is_shown(string source, string secret)
    {
        var ds = new DataSourceDefinition { Source = source };

        Assert.DoesNotContain(secret, ds.MaskedSource);
        Assert.DoesNotContain(secret, ds.DescribeForTrust(600));
    }

    [Fact]
    public void The_new_settings_are_saved_and_read_back_and_the_old_provider_numbers_are_unchanged()
    {
        var def = new ReportDefinition();
        def.DataSource = new DataSourceDefinition
        {
            Provider = DbProvider.Csv, Source = "a.csv", Selector = "s", Delimiter = ";", HasHeaderRow = false
        };

        var back = ReportDefinition.FromJson(def.ToJson()).DataSource;

        Assert.Equal((DbProvider.Csv, "a.csv", "s", ";", false), (back.Provider, back.Source, back.Selector, back.Delimiter, back.HasHeaderRow));
        Assert.Equal(4, (int)DbProvider.Host);
    }
}
