using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>Opening and running reports for the web viewer: trust rules, parameters, and what a visitor is told.</summary>
public sealed class ReportServiceTests : IDisposable
{
    private readonly string _dir = TestFiles.NewFolder();
    private readonly List<ReportService> _services = new();

    private static readonly IReadOnlyDictionary<string, string[]> NoValues = new Dictionary<string, string[]>();

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        TestFiles.Delete(_dir);
    }

    private ReportService Service(Action<WebViewerOptions>? tweak = null)
    {
        var o = new WebViewerOptions { ReportsFolder = _dir };
        tweak?.Invoke(o);
        var options = Options.Create(o);
        var service = new ReportService(new ReportCatalog(options), options, new WebTextMeasurer(TestFiles.FontsFolder()), NullLogger<ReportService>.Instance);
        _services.Add(service);
        return service;
    }

    private string Save(string id, ReportDefinition def)
    {
        var path = Path.Combine(_dir, id + ".rsr");
        def.Save(path);
        return path;
    }

    private static Dictionary<string, string[]> Values(string name, params string[] values) => new() { [name] = values };

    [Fact]
    public void A_report_in_the_folder_opens_and_a_missing_one_or_a_bad_name_does_not()
    {
        Save("sales", SampleReport.Create());
        var service = Service();

        Assert.Equal(RunStatus.Ok, service.Open("sales").Status);
        Assert.Equal(RunStatus.NotFound, service.Open("missing").Status);
        Assert.Equal(RunStatus.NotFound, service.Open("../sales").Status);
        Assert.Equal(RunStatus.NotFound, service.Open(null).Status);
    }

    [Fact]
    public async Task A_report_is_worked_out()
    {
        Save("sales", SampleReport.Create());

        var view = await Service().RunAsync("sales", NoValues, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Ok, view.Status);
        Assert.NotNull(view.Result);
        Assert.NotEmpty(view.Result.Pages);
        Assert.True(view.Result.RowCount > 0);
    }

    [Fact]
    public async Task A_parameter_value_reaches_the_report_whatever_its_case()
    {
        Save("sales", SampleReport.Create());

        var view = await Service().RunAsync("sales", Values("company", "Zed Corp"), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Ok, view.Status);
        Assert.Contains("Zed Corp", HtmlExporter.Build(view.Result!));
    }

    [Fact]
    public async Task A_parameter_the_report_does_not_have_is_a_bad_request()
    {
        Save("sales", SampleReport.Create());

        var view = await Service().RunAsync("sales", Values("Nope", "1"), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BadRequest, view.Status);
        Assert.Equal(400, view.HttpStatus);
        Assert.Contains("Nope", view.Message);
    }

    [Fact]
    public async Task A_value_that_is_not_a_number_is_a_bad_request()
    {
        var def = SampleReport.Create();
        def.Parameters.Add(new ReportParameter { Name = "Limit", Prompt = "Row limit", Type = ParameterType.Number, DefaultValue = "10" });
        Save("sales", def);

        var view = await Service().RunAsync("sales", Values("Limit", "abc"), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BadRequest, view.Status);
        Assert.Contains("Row limit", view.Message);
    }

    [Fact]
    public async Task A_very_long_value_is_a_bad_request()
    {
        Save("sales", SampleReport.Create());

        var view = await Service().RunAsync("sales", Values("Company", new string('x', ReportService.MaxParameterChars + 1)), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BadRequest, view.Status);
    }

    [Fact]
    public void A_report_with_its_own_database_query_is_refused_unless_the_server_trusts_reports()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.SqlServer;
        def.DataSource.ConnectionString = "Server=nowhere;Database=x;Password=hunter2";
        def.DataSource.Query = "SELECT 1 AS Marker";
        Save("q", def);

        var refused = Service().Open("q");
        Assert.Equal(RunStatus.Refused, refused.Status);
        Assert.Equal(403, refused.HttpStatus);
        Assert.Contains("TrustReportQueries", refused.Message);
        // the visitor is not shown what the report would run
        Assert.DoesNotContain("SELECT 1", refused.Message);
        Assert.DoesNotContain("hunter2", refused.Message);

        Assert.Equal(RunStatus.Ok, Service(o => o.TrustReportQueries = true).Open("q").Status);
    }

    [Fact]
    public void A_report_that_reads_a_data_file_needs_the_server_to_allow_it()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = "data.csv";
        Save("f", def);

        var refused = Service().Open("f");
        Assert.Equal(RunStatus.Refused, refused.Status);
        Assert.Contains("AllowDataFiles", refused.Message);

        Assert.Equal(RunStatus.Ok, Service(o => o.AllowDataFiles = true).Open("f").Status);
    }

    [Fact]
    public void A_report_that_reads_a_web_address_needs_the_server_to_allow_it()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Rest;
        def.DataSource.Source = "https://example.com/data.json";
        Save("w", def);

        Assert.Contains("AllowWebData", Service().Open("w").Message);
        Assert.Equal(RunStatus.Ok, Service(o => o.AllowWebData = true).Open("w").Status);
    }

    [Fact]
    public void A_report_that_needs_host_data_is_refused()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Host;
        Save("h", def);

        var view = Service(o => { o.TrustReportQueries = true; o.AllowDataFiles = true; o.AllowWebData = true; }).Open("h");

        Assert.Equal(RunStatus.Refused, view.Status);
        Assert.Contains("host application", view.Message);
    }

    [Fact]
    public void A_damaged_report_file_is_reported_without_its_path()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.rsr"), "{ this is not json");

        var view = Service().Open("bad");

        Assert.Equal(RunStatus.Failed, view.Status);
        Assert.Equal(500, view.HttpStatus);
        Assert.DoesNotContain(_dir, view.Message);
    }

    [Fact]
    public async Task A_report_can_read_a_csv_file_when_the_server_allows_it()
    {
        File.WriteAllText(Path.Combine(_dir, "data.csv"), "Name,Amount\nA,10\nB,20\n");
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = "data.csv";
        Save("f", def);

        var view = await Service(o => o.AllowDataFiles = true).RunAsync("f", NoValues, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Ok, view.Status);
    }

    [Fact]
    public async Task A_data_file_outside_the_data_root_is_not_read()
    {
        File.WriteAllText(Path.Combine(_dir, "data.csv"), "Name,Amount\nA,10\n");
        Directory.CreateDirectory(Path.Combine(_dir, "safe"));
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = "data.csv";
        Save("f", def);

        var view = await Service(o => { o.AllowDataFiles = true; o.DataRoot = Path.Combine(_dir, "safe"); })
            .RunAsync("f", NoValues, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Failed, view.Status);
        Assert.DoesNotContain(_dir, view.Message);
    }
}
