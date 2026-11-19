using Microsoft.Extensions.Options;
using ReportStudio.Core.Samples;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>The reports folder of the web viewer: a report is a file name and nothing that can point elsewhere.</summary>
public sealed class ReportCatalogTests : IDisposable
{
    private readonly string _dir = TestFiles.NewFolder();

    public void Dispose() => TestFiles.Delete(_dir);

    private ReportCatalog Catalog(int maxKb = 4096) =>
        new(Options.Create(new WebViewerOptions { ReportsFolder = _dir, MaxReportFileKb = maxKb }));

    [Theory]
    [InlineData("sales")]
    [InlineData("Sales Report 2026")]
    [InlineData("a-b_c.(1)")]
    public void Plain_names_are_valid(string id) => Assert.True(ReportCatalog.IsValidId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("C:evil")]
    [InlineData("a%2fb")]
    [InlineData("a;b")]
    public void Names_that_could_point_elsewhere_are_not_valid(string? id) => Assert.False(ReportCatalog.IsValidId(id));

    [Fact]
    public void A_name_that_is_too_long_is_not_valid()
    {
        Assert.False(ReportCatalog.IsValidId(new string('a', ReportCatalog.MaxIdLength + 1)));
        Assert.True(ReportCatalog.IsValidId(new string('a', ReportCatalog.MaxIdLength)));
    }

    [Fact]
    public void A_report_in_the_folder_is_found()
    {
        SampleReport.Create().Save(Path.Combine(_dir, "sales.rsr"));
        var path = Catalog().Resolve("sales");
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_missing_report_or_a_way_out_of_the_folder_is_not_found()
    {
        SampleReport.Create().Save(Path.Combine(_dir, "sales.rsr"));
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "outside.rsr");
        SampleReport.Create().Save(outside);
        try
        {
            var catalog = Catalog();
            Assert.Null(catalog.Resolve("nothing"));
            Assert.Null(catalog.Resolve("../outside"));
            Assert.Null(catalog.Resolve(outside));
            Assert.Null(catalog.Resolve("sales/../sales"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void The_list_has_only_report_files_sorted_with_their_titles()
    {
        SampleReport.Create().Save(Path.Combine(_dir, "b-sales.rsr"));
        SampleReport.Create().Save(Path.Combine(_dir, "a-sales.rsr"));
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a report");
        File.WriteAllText(Path.Combine(_dir, "bad;name.rsr"), "{}");

        var list = Catalog().List();

        Assert.Equal(2, list.Count);
        Assert.Equal("a-sales", list[0].Id);
        Assert.Equal("b-sales", list[1].Id);
        Assert.Equal("Sales by Country and Customer", list[0].Title);
    }

    [Fact]
    public void A_damaged_report_is_listed_under_its_file_name()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.rsr"), "{ this is not json");

        var entry = Assert.Single(Catalog().List());
        Assert.Equal("broken", entry.Id);
        Assert.Equal("broken", entry.Title);
    }

    [Fact]
    public void A_missing_folder_gives_an_empty_list()
    {
        var catalog = new ReportCatalog(Options.Create(new WebViewerOptions { ReportsFolder = Path.Combine(_dir, "nowhere") }));
        Assert.Empty(catalog.List());
    }

    [Fact]
    public void A_report_file_over_the_size_limit_is_not_opened()
    {
        var path = Path.Combine(_dir, "big.rsr");
        File.WriteAllText(path, new string(' ', 3000));

        Assert.Throws<InvalidDataException>(() => Catalog(maxKb: 1).Load(path));
    }
}
