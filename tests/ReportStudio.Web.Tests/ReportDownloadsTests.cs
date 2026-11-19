using System.Text;
using ReportStudio.Core.Formatting;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Samples;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>The download formats of the web viewer, written in memory from the sample report.</summary>
public class ReportDownloadsTests
{
    private static readonly WebTextMeasurer Measurer = new(TestFiles.FontsFolder());

    private static ReportResult Sample() =>
        ReportEngine.Run(SampleReport.Create(), new Dictionary<string, string?>(), new ReportOptions { Measurer = Measurer });

    [Fact]
    public void The_formats_have_distinct_names_and_extensions()
    {
        Assert.Equal(ReportDownloads.Formats.Count, ReportDownloads.Formats.Select(f => f.Id).Distinct().Count());
        Assert.Equal(ReportDownloads.Formats.Count, ReportDownloads.Formats.Select(f => f.Extension).Distinct().Count());
        Assert.All(ReportDownloads.Formats, f => Assert.False(string.IsNullOrWhiteSpace(f.ContentType)));
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("XLSX")]
    [InlineData("csv")]
    public void A_format_is_found_by_name_whatever_the_case(string name) => Assert.NotNull(ReportDownloads.Find(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pdf")]
    [InlineData("exe")]
    [InlineData("../csv")]
    public void Anything_else_is_not_a_format(string? name) => Assert.Null(ReportDownloads.Find(name));

    [Fact]
    public void Every_format_writes_something()
    {
        var result = Sample();
        foreach (var format in ReportDownloads.Formats)
        {
            var bytes = ReportDownloads.Write(format, result, IndianFormat.Culture);
            Assert.True(bytes.Length > 20, $"{format.Id} wrote only {bytes.Length} bytes");
        }
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("docx")]
    [InlineData("odt")]
    [InlineData("ods")]
    public void The_zip_based_formats_start_with_a_zip_header(string id)
    {
        var bytes = ReportDownloads.Write(ReportDownloads.Find(id)!, Sample(), IndianFormat.Culture);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public void Rtf_starts_with_the_rtf_group_and_html_is_a_page()
    {
        var result = Sample();
        var rtf = ReportDownloads.Write(ReportDownloads.Find("rtf")!, result, IndianFormat.Culture);
        Assert.Equal("{\\rtf", Encoding.ASCII.GetString(rtf, 0, 5));

        var html = Encoding.UTF8.GetString(ReportDownloads.Write(ReportDownloads.Find("html")!, result, IndianFormat.Culture));
        Assert.Contains("<html", html);
    }
}
