using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Mixed page sizes in one report (requirement 38): a group that starts a new page can set the size of its pages.</summary>
public class PageSizeTests
{
    private const double Landscape = 1123, Portrait = 794;

    // Report page 794 x 1123 with no margins; a page footer 20 high; one detail line of 40.
    private static ReportDefinition Definition(params GroupDefinition[] groups)
    {
        var def = new ReportDefinition { Name = "Sizes" };
        def.DataSource.Provider = DbProvider.Host;
        def.Page.Width = 794; def.Page.Height = 1123;
        def.Page.MarginLeft = 0; def.Page.MarginRight = 0; def.Page.MarginTop = 0; def.Page.MarginBottom = 0;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0;
        def.PageFooter.Height = 20;
        def.PageFooter.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "F", Width = 50, Height = 18 });
        def.Detail.Height = 40;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Sec}{Sub}", Width = 100, Height = 30 });
        def.Groups.AddRange(groups);
        return def;
    }

    private static GroupDefinition Group(string expression, bool newPage = true, double w = 0, double h = 0, string when = "") =>
        new() { Expression = expression, StartNewPage = newPage, PageWidth = w, PageHeight = h, PageSizeWhen = when };

    private static ReportResult Run(ReportDefinition def, params (string Sec, string Sub)[] rows) =>
        ReportEngine.Run(def, null,
            rows.Select(r => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Sec"] = r.Sec, ["Sub"] = r.Sub }),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static (double, double)[] Sizes(ReportResult result) => result.Pages.Select(p => (p.Width, p.Height)).ToArray();

    [Fact]
    public void A_group_can_set_the_size_of_the_pages_it_starts_when_a_condition_holds()
    {
        var def = Definition(Group("Sec", w: Landscape, h: Portrait, when: "Sec == 'B'"));

        var result = Run(def, ("A", ""), ("B", ""), ("C", ""));

        Assert.Equal(new[] { (Portrait, 1123.0), (Landscape, Portrait), (Portrait, 1123.0) }, Sizes(result));
    }

    [Fact]
    public void Without_a_condition_the_size_applies_to_every_instance()
    {
        var def = Definition(Group("Sec", w: Landscape, h: Portrait));

        var result = Run(def, ("A", ""), ("B", ""));

        Assert.Equal(new[] { (Landscape, Portrait), (Landscape, Portrait) }, Sizes(result));
    }

    [Fact]
    public void A_group_on_the_first_page_changes_that_page_instead_of_leaving_it_blank()
    {
        var def = Definition(Group("Sec", w: Landscape, h: Portrait, when: "Sec == 'B'"));

        var result = Run(def, ("B", ""), ("B", ""));

        var page = Assert.Single(result.Pages);
        Assert.Equal((Landscape, Portrait), (page.Width, page.Height));
        Assert.Equal(1, page.Number);
        Assert.Equal(2, page.Items.Count(i => i.Band == BandKind.Detail));
    }

    [Fact]
    public void The_page_footer_sits_at_the_bottom_of_each_pages_own_size()
    {
        var def = Definition(Group("Sec", w: Landscape, h: Portrait, when: "Sec == 'B'"));

        var result = Run(def, ("A", ""), ("B", ""));

        Assert.Equal(1103, result.Pages[0].Items.Single(i => i.Text == "F").Y);
        Assert.Equal(774, result.Pages[1].Items.Single(i => i.Text == "F").Y);
    }

    [Fact]
    public void An_inner_group_without_a_size_keeps_the_size_its_outer_group_set()
    {
        var def = Definition(Group("Sec", w: Landscape, h: Portrait, when: "Sec == 'B'"), Group("Sub"));

        var result = Run(def, ("A", "x"), ("B", "x"), ("B", "y"));

        Assert.Equal(new[] { (Portrait, 1123.0), (Landscape, Portrait), (Landscape, Portrait) }, Sizes(result));
    }

    [Fact]
    public void A_group_that_does_not_start_a_new_page_cannot_change_the_size()
    {
        var def = Definition(Group("Sec", newPage: false, w: Landscape, h: Portrait));

        var result = Run(def, ("A", ""), ("B", ""));

        Assert.Equal(new[] { (Portrait, 1123.0) }, Sizes(result));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(-5, 800)]
    [InlineData(0, 800)]
    [InlineData(1123, 0)]
    [InlineData(99999, 800)]
    public void A_size_that_makes_no_sense_leaves_the_report_page(double width, double height)
    {
        var def = Definition(Group("Sec", w: width, h: height));

        var result = Run(def, ("A", ""), ("B", ""));

        Assert.All(result.Pages, p => Assert.Equal((Portrait, 1123.0), (p.Width, p.Height)));
    }

    [Fact]
    public void A_condition_that_cannot_be_worked_out_gives_a_warning_and_the_usual_page()
    {
        var def = Definition(Group("Sec", w: Landscape, h: Portrait, when: "NoSuchFunction(1)"));

        var result = Run(def, ("A", ""));

        Assert.Contains(result.Warnings, w => w.Contains("Page size condition"));
        Assert.Equal((Portrait, 1123.0), (result.Pages[0].Width, result.Pages[0].Height));
    }

    [Fact]
    public void A_page_size_keeps_the_margins_and_columns()
    {
        var page = new PageSettings
        {
            MarginLeft = 11, MarginRight = 12, MarginTop = 13, MarginBottom = 14,
            Columns = 3, ColumnGap = 7, ColumnOrder = ColumnOrder.Across
        };

        var wide = page.WithSize(1123, 794);

        Assert.Equal((1123.0, 794.0), (wide.Width, wide.Height));
        Assert.Equal((11.0, 12.0, 13.0, 14.0), (wide.MarginLeft, wide.MarginRight, wide.MarginTop, wide.MarginBottom));
        Assert.Equal((3, 7.0, ColumnOrder.Across), (wide.Columns, wide.ColumnGap, wide.ColumnOrder));
    }

    [Fact]
    public void A_report_without_group_sizes_is_unchanged()
    {
        var result = Run(Definition(Group("Sec")), ("A", ""), ("B", ""));

        Assert.All(result.Pages, p => Assert.Equal((Portrait, 1123.0), (p.Width, p.Height)));
    }
}
