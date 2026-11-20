using Microsoft.Extensions.Options;
using ReportStudio.Core.Samples;
using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>The designer keeps versions in a ".history" folder beside the reports; the web viewer must not show or open them (requirement 51b).</summary>
public sealed class HistoryFolderTests : IDisposable
{
    private readonly string _dir = TestFiles.NewFolder();

    public void Dispose() => TestFiles.Delete(_dir);

    private ReportCatalog Catalog() => new(Options.Create(new WebViewerOptions { ReportsFolder = _dir }));

    [Fact]
    public void Versions_are_not_listed_and_cannot_be_opened_as_reports()
    {
        var def = SampleReport.Create();
        def.Save(Path.Combine(_dir, "sales.rsr"));
        new ReportHistory(Path.Combine(_dir, ".history")).Record("sales", def.ToJson(), "tester", "first");

        var catalog = Catalog();
        Assert.Equal(["sales"], catalog.List().Select(e => e.Id).ToArray());
        Assert.Null(catalog.Resolve(".history"));
        Assert.Null(catalog.Resolve(".history/sales/v0001"));
        Assert.Null(catalog.Resolve("v0001"));
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("Sales Report 2026")]
    [InlineData("a-b_c.(1)")]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("a/b")]
    [InlineData("C:evil")]
    public void The_history_and_the_catalog_agree_on_which_names_are_allowed(string id) =>
        Assert.Equal(ReportCatalog.IsValidId(id), ReportHistory.IsValidId(id));
}
