using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Watermark (requirement 34, third part).</summary>
public class WatermarkTests
{
    // ---- where it goes and how big it may be ----

    [Theory]
    [InlineData(800, 600, 0, 800)]          // straight: the page width
    [InlineData(800, 600, 180, 800)]
    [InlineData(800, 600, 90, 600)]         // upright: the page height
    [InlineData(800, 600, -90, 600)]
    public void The_room_along_a_straight_line_is_the_page_side(double width, double height, double angle, double expected)
    {
        Assert.Equal(expected, WatermarkLayout.AvailableLength(width, height, angle), 6);
    }

    [Fact]
    public void The_room_along_a_diagonal_is_the_shorter_way_out_of_the_page()
    {
        // at 45 degrees the line leaves through the nearer of the two sides: 600 / sin 45
        Assert.Equal(600 / Math.Sin(Math.PI / 4), WatermarkLayout.AvailableLength(800, 600, 45), 6);
        Assert.Equal(WatermarkLayout.AvailableLength(800, 600, 45), WatermarkLayout.AvailableLength(800, 600, -45), 6);
    }

    [Fact]
    public void A_text_that_fits_keeps_its_size()
    {
        Assert.Equal(72, WatermarkLayout.FitFontSize(72, 500, 800, 600, 0));       // room is 720
    }

    [Fact]
    public void A_text_that_is_too_wide_is_made_smaller_until_it_fits_nine_tenths_of_the_room()
    {
        // room = 720; the text is 1440 wide at size 100, so it must be half the size
        Assert.Equal(50, WatermarkLayout.FitFontSize(100, 1440, 800, 600, 0), 6);
    }

    [Fact]
    public void The_size_never_grows_and_never_drops_below_four()
    {
        Assert.Equal(60, WatermarkLayout.FitFontSize(60, 10, 800, 600, 0));
        Assert.Equal(4, WatermarkLayout.FitFontSize(60, 1_000_000, 800, 600, 0));
        Assert.Equal(3, WatermarkLayout.FitFontSize(3, 1_000_000, 800, 600, 0));   // asked for less than 4: stays as asked
    }

    [Fact]
    public void A_text_without_a_width_keeps_its_size()
    {
        Assert.Equal(72, WatermarkLayout.FitFontSize(72, 0, 800, 600, 45));
        Assert.Equal(72, WatermarkLayout.FitFontSize(72, double.NaN, 800, 600, 45));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 255)]
    [InlineData(0.5, 128)]
    [InlineData(2, 255)]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    public void Opacity_becomes_an_alpha_value(double opacity, int alpha)
    {
        Assert.Equal(alpha, WatermarkLayout.Alpha(opacity));
    }

    // ---- the settings ----

    [Fact]
    public void A_new_report_has_no_watermark_and_sensible_settings()
    {
        var wm = new ReportDefinition().Watermark;

        Assert.Equal("", wm.Text);
        Assert.Equal("#9CA3AF", wm.Color);
        Assert.Equal(0.3, wm.Opacity);
        Assert.Equal(45, wm.Angle);
        Assert.Equal(72, wm.FontSize);
        Assert.True(wm.Bold);
    }

    [Fact]
    public void Odd_values_are_brought_into_range()
    {
        var wm = new WatermarkSettings { Text = null!, Color = "  ", Opacity = 7, Angle = 9000, FontSize = 1 };
        wm.Normalize();

        Assert.Equal("", wm.Text);
        Assert.Equal(WatermarkSettings.DefaultColor, wm.Color);
        Assert.Equal(1, wm.Opacity);
        Assert.Equal(360, wm.Angle);
        Assert.Equal(6, wm.FontSize);

        wm.Opacity = double.NaN; wm.Angle = double.PositiveInfinity; wm.FontSize = double.NaN;
        wm.Normalize();

        Assert.Equal(0.3, wm.Opacity);
        Assert.Equal(45, wm.Angle);
        Assert.Equal(72, wm.FontSize);
    }

    [Fact]
    public void The_settings_survive_saving()
    {
        var def = new ReportDefinition();
        def.Watermark.Text = "CONFIDENTIAL {@Who}";
        def.Watermark.Color = "#FF0000";
        def.Watermark.Opacity = 0.15;
        def.Watermark.Angle = -30;
        def.Watermark.FontSize = 48;
        def.Watermark.Bold = false;

        var back = ReportDefinition.FromJson(def.ToJson()).Watermark;

        Assert.Equal("CONFIDENTIAL {@Who}", back.Text);
        Assert.Equal("#FF0000", back.Color);
        Assert.Equal(0.15, back.Opacity);
        Assert.Equal(-30, back.Angle);
        Assert.Equal(48, back.FontSize);
        Assert.False(back.Bold);
    }

    [Fact]
    public void A_file_with_no_watermark_section_or_a_null_one_loads_without_a_watermark()
    {
        var old = ReportDefinition.FromJson("""{ "Name": "Old report" }""");
        Assert.Equal("", old.Watermark.Text);

        var nulled = ReportDefinition.FromJson("""{ "Name": "x", "Watermark": null }""");
        Assert.NotNull(nulled.Watermark);
        Assert.Equal("", nulled.Watermark.Text);
    }

    // ---- in a report ----

    private static ReportDefinition Definition(string text)
    {
        var def = new ReportDefinition { Name = "Watermarked" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{A}", Width = 100, Height = 20 });
        def.Parameters.Add(new ReportParameter { Name = "Status", Type = ParameterType.Text, DefaultValue = "DRAFT" });
        def.Watermark.Text = text;
        return def;
    }

    private static ReportResult Run(ReportDefinition def, int rows = 1, IDictionary<string, string?>? values = null) =>
        ReportEngine.Run(def, values,
            Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = i }),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

    [Fact]
    public void Every_page_carries_the_watermark_with_its_settings()
    {
        var def = Definition("DRAFT");
        def.Watermark.Color = "#112233"; def.Watermark.Opacity = 0.2; def.Watermark.Angle = 30;
        def.Watermark.FontSize = 60; def.Watermark.Bold = false;

        var result = Run(def, rows: 100);

        Assert.True(result.Pages.Count >= 2, "the report needs more than one page for this test");
        Assert.All(result.Pages, page =>
        {
            var wm = Assert.IsType<RenderedWatermark>(page.Watermark);
            Assert.Equal("DRAFT", wm.Text);
            Assert.Equal("#112233", wm.Color);
            Assert.Equal(0.2, wm.Opacity);
            Assert.Equal(30, wm.Angle);
            Assert.Equal(60, wm.FontSize);
            Assert.False(wm.Bold);
        });
    }

    [Fact]
    public void Expressions_in_the_text_are_filled_in()
    {
        var result = Run(Definition("{@Status} copy"), values: new Dictionary<string, string?> { ["Status"] = "FINAL" });

        Assert.Equal("FINAL copy", result.Pages[0].Watermark!.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_page_number_can_be_part_of_the_text()
    {
        var result = Run(Definition("Page {Page}"), rows: 100);

        Assert.Equal("Page 1", result.Pages[0].Watermark!.Text);
        Assert.Equal("Page 2", result.Pages[1].Watermark!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_text_means_no_watermark(string text)
    {
        Assert.All(Run(Definition(text)).Pages, page => Assert.Null(page.Watermark));
    }

    [Fact]
    public void A_text_that_works_out_empty_means_no_watermark()
    {
        var def = Definition("{@Status}");
        def.Parameters[0].DefaultValue = "";

        Assert.All(Run(def, values: new Dictionary<string, string?> { ["Status"] = "" }).Pages, page => Assert.Null(page.Watermark));
    }

    [Fact]
    public void A_fully_see_through_watermark_is_left_out()
    {
        var def = Definition("DRAFT");
        def.Watermark.Opacity = 0;

        Assert.All(Run(def).Pages, page => Assert.Null(page.Watermark));
    }

    [Fact]
    public void A_broken_expression_warns_and_the_report_still_runs()
    {
        var result = Run(Definition("{NoSuchField}"));

        Assert.NotEmpty(result.Pages);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal("#ERR", result.Pages[0].Watermark!.Text);
    }

    [Fact]
    public void The_watermark_does_not_change_the_layout()
    {
        var without = Run(Definition(""), rows: 100);
        var with = Run(Definition("DRAFT"), rows: 100);

        Assert.Equal(without.Pages.Count, with.Pages.Count);
        Assert.Equal(without.Pages.Sum(p => p.Items.Count), with.Pages.Sum(p => p.Items.Count));
    }
}
