using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Pages of different sizes in one report (requirement 38) in the HTML output.</summary>
public class PageSizeExportTests
{
    private static string Html(params (double Width, double Height)[] pages)
    {
        var result = new ReportResult { ReportName = "Sizes" };
        for (int i = 0; i < pages.Length; i++)
            result.Pages.Add(new RenderedPage { Number = i + 1, Width = pages[i].Width, Height = pages[i].Height });
        return HtmlExporter.Build(result);
    }

    private static int Occurrences(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

    [Fact]
    public void Pages_of_one_size_get_no_named_pages()
    {
        var html = Html((794, 1123), (794, 1123));

        Assert.DoesNotContain("@page s0", html);
        Assert.DoesNotContain("page:s", html);
        Assert.Contains("@page{size:794px 1123px;margin:0}", html);
    }

    [Fact]
    public void Every_size_gets_a_named_page_when_sizes_are_mixed()
    {
        var html = Html((794, 1123), (1123, 794), (794, 1123));

        Assert.Contains("@page s0{size:794px 1123px;margin:0}", html);
        Assert.Contains("@page s1{size:1123px 794px;margin:0}", html);
        Assert.DoesNotContain("@page s2", html);
    }

    [Fact]
    public void Each_page_uses_the_named_page_of_its_own_size()
    {
        var html = Html((794, 1123), (1123, 794), (794, 1123));

        Assert.Equal(2, Occurrences(html, "width:794px;height:1123px;page:s0"));
        Assert.Equal(1, Occurrences(html, "width:1123px;height:794px;page:s1"));
    }
}
