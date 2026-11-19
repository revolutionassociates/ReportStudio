using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>The web viewer end to end: addresses, security headers, escaping, and what a visitor can and cannot reach.</summary>
public sealed class WebAppTests : IDisposable
{
    private readonly string _dir = TestFiles.NewFolder();
    private readonly WebApplicationFactory<Program> _factory;

    public WebAppTests()
    {
        SampleReport.Create().Save(Path.Combine(_dir, "sales.rsr"));

        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.SqlServer;
        def.DataSource.Query = "SELECT 1 AS Marker";
        def.Save(Path.Combine(_dir, "query.rsr"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Configure<WebViewerOptions>(o => o.ReportsFolder = _dir)));
    }

    public void Dispose()
    {
        _factory.Dispose();
        TestFiles.Delete(_dir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_home_page_lists_the_reports()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sales by Country and Customer", html);
        Assert.Contains("href=\"/r/sales\"", html);
    }

    [Fact]
    public async Task Every_page_carries_the_security_headers()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/", Ct);

        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task The_health_address_answers()
    {
        using var client = _factory.CreateClient();
        Assert.Equal("ok", await client.GetStringAsync("/healthz", Ct));
    }

    [Fact]
    public async Task A_report_with_parameters_shows_a_form_with_the_defaults()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/r/sales", Ct);

        Assert.Contains("name=\"p.Company\"", html);
        Assert.Contains("Acme Industries", html);
        Assert.Contains("action=\"/r/sales/view\"", html);
    }

    [Fact]
    public async Task A_report_is_shown_with_the_value_that_was_typed()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/r/sales/view?p.Company=Zed%20Corp", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Zed Corp", html);
        Assert.Contains("rs-bar", html);
    }

    [Fact]
    public async Task Markup_typed_into_a_parameter_is_not_written_into_the_page_as_markup()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/r/sales/view?p.Company=%3Cscript%3Ealert(1)%3C%2Fscript%3E", Ct);

        Assert.DoesNotContain("<script>alert(1)", html);
    }

    [Fact]
    public async Task An_unknown_parameter_is_a_bad_request()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/r/sales/view?p.Nope=1", Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/r/missing")]
    [InlineData("/r/missing/view")]
    [InlineData("/r/..%2f..%2fetc%2fpasswd")]
    [InlineData("/r/..%5csales/view")]
    public async Task A_report_that_is_not_in_the_folder_is_not_found(string address)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(address, Ct);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_report_with_its_own_query_is_forbidden_and_its_query_is_not_shown()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/r/query/view", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("SELECT 1", html);
    }

    [Fact]
    public async Task The_report_page_offers_every_download_with_the_typed_values_kept()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/r/sales/view?p.Company=Zed%20Corp", Ct);

        foreach (var format in ReportDownloads.Formats)
            Assert.Contains($"href=\"/r/sales/download/{format.Id}?p.Company=Zed%20Corp\"", html);
    }

    [Theory]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("csv", "text/csv")]
    [InlineData("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("rtf", "application/rtf")]
    [InlineData("odt", "application/vnd.oasis.opendocument.text")]
    [InlineData("ods", "application/vnd.oasis.opendocument.spreadsheet")]
    public async Task Each_download_is_a_file_of_the_right_type(string format, string contentType)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/r/sales/download/{format}?p.Company=Zed%20Corp", Ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sales." + format, response.Content.Headers.ContentDisposition?.FileName);
        Assert.True(bytes.Length > 20);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("exe")]
    [InlineData("..%2fcsv")]
    public async Task A_download_format_that_is_not_offered_is_not_found(string format)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/r/sales/download/{format}", Ct);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_download_of_a_report_that_may_not_run_is_forbidden()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/r/query/download/csv", Ct);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_download_with_an_unknown_parameter_is_a_bad_request()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/r/sales/download/csv?p.Nope=1", Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_html_download_is_a_plain_html_file_without_the_viewer_bar()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/r/sales/download/html?p.Company=Zed%20Corp", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sales.html", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Contains("Zed Corp", html);
        Assert.DoesNotContain("rs-bar", html);
    }
}
