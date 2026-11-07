using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Page background (requirement 34, fourth part).</summary>
public class BackgroundTests
{
    // ---- the settings ----

    [Fact]
    public void A_new_report_has_a_plain_page()
    {
        var b = new ReportDefinition().Background;

        Assert.Equal("", b.Color);
        Assert.Equal("", b.GradientColor);
        Assert.Equal(GradientDirection.TopToBottom, b.Direction);
    }

    [Fact]
    public void Normalizing_trims_the_colours_and_replaces_missing_ones()
    {
        var b = new PageBackgroundSettings { Color = "  #FFF8E1 ", GradientColor = null! };
        b.Normalize();

        Assert.Equal("#FFF8E1", b.Color);
        Assert.Equal("", b.GradientColor);

        b.Color = null!;
        b.Normalize();
        Assert.Equal("", b.Color);
    }

    [Fact]
    public void The_settings_survive_saving()
    {
        var def = new ReportDefinition();
        def.Background.Color = "#FFF8E1";
        def.Background.GradientColor = "#FFE0B2";
        def.Background.Direction = GradientDirection.LeftToRight;

        var back = ReportDefinition.FromJson(def.ToJson()).Background;

        Assert.Equal("#FFF8E1", back.Color);
        Assert.Equal("#FFE0B2", back.GradientColor);
        Assert.Equal(GradientDirection.LeftToRight, back.Direction);
    }

    [Fact]
    public void A_file_with_no_background_section_or_a_null_one_loads_with_a_plain_page()
    {
        var old = ReportDefinition.FromJson("""{ "Name": "Old report" }""");
        Assert.Equal("", old.Background.Color);

        var nulled = ReportDefinition.FromJson("""{ "Name": "x", "Background": null }""");
        Assert.NotNull(nulled.Background);
        Assert.Equal("", nulled.Background.Color);
    }

    // ---- in a report ----

    private static ReportDefinition Definition(string color, string gradient = "", GradientDirection direction = GradientDirection.TopToBottom)
    {
        var def = new ReportDefinition { Name = "Background" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{A}", Width = 100, Height = 20 });
        def.Background.Color = color;
        def.Background.GradientColor = gradient;
        def.Background.Direction = direction;
        return def;
    }

    private static ReportResult Run(ReportDefinition def, int rows = 1) =>
        ReportEngine.Run(def, null,
            Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = i }),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

    [Fact]
    public void Every_page_carries_the_background()
    {
        var result = Run(Definition("#FFF8E1", "#FFE0B2", GradientDirection.LeftToRight), rows: 100);

        Assert.True(result.Pages.Count >= 2, "the report needs more than one page for this test");
        Assert.All(result.Pages, page =>
        {
            var b = Assert.IsType<RenderedBackground>(page.Background);
            Assert.Equal("#FFF8E1", b.Color);
            Assert.Equal("#FFE0B2", b.GradientColor);
            Assert.Equal(GradientDirection.LeftToRight, b.GradientDirection);
        });
    }

    [Fact]
    public void A_single_colour_has_no_gradient_colour()
    {
        var b = Run(Definition("#FFF8E1")).Pages[0].Background!;

        Assert.Equal("#FFF8E1", b.Color);
        Assert.Equal("", b.GradientColor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_colour_means_a_plain_page(string color)
    {
        Assert.All(Run(Definition(color)).Pages, page => Assert.Null(page.Background));
    }

    [Fact]
    public void A_gradient_colour_alone_is_not_enough()
    {
        Assert.All(Run(Definition("", "#FFE0B2")).Pages, page => Assert.Null(page.Background));
    }

    [Fact]
    public void The_background_does_not_change_the_layout()
    {
        var without = Run(Definition(""), rows: 100);
        var with = Run(Definition("#FFF8E1"), rows: 100);

        Assert.Equal(without.Pages.Count, with.Pages.Count);
        Assert.Equal(without.Pages.Sum(p => p.Items.Count), with.Pages.Sum(p => p.Items.Count));
    }
}
