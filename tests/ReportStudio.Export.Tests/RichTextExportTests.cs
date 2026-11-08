using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Text;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Rich text (requirement 36, first part) in the HTML output.</summary>
public class RichTextExportTests
{
    private static string Html(string text, params TextRun[]? runs)
    {
        var result = new ReportResult { ReportName = "Rich" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.Add(new RenderedItem
        {
            Kind = ItemKind.Text, X = 10, Y = 10, Width = 300, Height = 30, Text = text,
            Runs = runs is { Length: > 0 } ? runs.ToList() : null
        });
        result.Pages.Add(page);
        return HtmlExporter.Build(result);
    }

    [Fact]
    public void Each_run_becomes_a_span_with_only_its_own_look()
    {
        var html = Html("Total: 5 only", new TextRun("Total: "), new TextRun("5", Bold: true), new TextRun(" only"));

        Assert.Contains("<div>Total: <span style=\"font-weight:700;\">5</span> only</div>", html);
    }

    [Fact]
    public void Italic_underline_strike_and_colour_are_written_as_css()
    {
        Assert.Contains("<span style=\"font-style:italic;\">x</span>", Html("x", new TextRun("x", Italic: true)));
        Assert.Contains("<span style=\"text-decoration:underline;\">x</span>", Html("x", new TextRun("x", Underline: true)));
        Assert.Contains("<span style=\"text-decoration:line-through;\">x</span>", Html("x", new TextRun("x", Strike: true)));
        Assert.Contains("<span style=\"text-decoration:underline line-through;\">x</span>", Html("x", new TextRun("x", Underline: true, Strike: true)));
        Assert.Contains("<span style=\"color:#cc0000;\">x</span>", Html("x", new TextRun("x", Color: "#CC0000")).ToLowerInvariant());
    }

    [Fact]
    public void Looks_come_in_one_fixed_order()
    {
        var html = Html("x", new TextRun("x", Bold: true, Italic: true, Underline: true, Color: "#0000FF")).ToLowerInvariant();

        Assert.Contains("<span style=\"font-weight:700;font-style:italic;text-decoration:underline;color:#0000ff;\">x</span>", html);
    }

    [Fact]
    public void Run_text_is_always_encoded_so_it_cannot_add_a_tag()
    {
        var html = Html("<script>alert(1)</script>", new TextRun("<script>alert(1)</script>", Bold: true));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public void A_colour_that_is_not_one_adds_no_colour()
    {
        var html = Html("x", new TextRun("x", Bold: true, Color: "expression(alert(1))"));

        Assert.DoesNotContain("expression", html);
        Assert.Contains("<span style=\"font-weight:700;\">x</span>", html);
    }

    [Fact]
    public void Text_without_runs_is_written_as_before()
    {
        var html = Html("a < b & c");

        Assert.Contains("<div>a &lt; b &amp; c</div>", html);
        Assert.DoesNotContain("<span", html);
    }

    [Fact]
    public void A_line_break_in_a_run_stays_a_line_break_in_the_text()
    {
        var html = Html("a\nb", new TextRun("a\n", Bold: true), new TextRun("b"));

        Assert.Contains("<span style=\"font-weight:700;\">a\n</span>b", html);
    }
}
