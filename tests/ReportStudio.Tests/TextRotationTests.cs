using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Rotated text (requirement 34, fifth part): the shared rules and the engine.</summary>
public class TextRotationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void The_angle_is_brought_into_one_turn(double given, double expected)
    {
        Assert.Equal(expected, TextTurn.Normalize(given), 6);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(360, false)]
    [InlineData(0.0005, false)]
    [InlineData(359.9995, false)]
    [InlineData(1, true)]
    [InlineData(90, true)]
    [InlineData(-45, true)]
    [InlineData(double.NaN, false)]
    public void Only_a_real_turn_counts_as_turned(double angle, bool expected)
    {
        Assert.Equal(expected, TextTurn.IsTurned(angle));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(44, false)]
    [InlineData(46, true)]
    [InlineData(90, true)]
    [InlineData(134, true)]
    [InlineData(136, false)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    [InlineData(-90, true)]
    public void Text_near_a_quarter_turn_runs_along_the_height(double angle, bool sideways)
    {
        Assert.Equal(sideways, TextTurn.IsSideways(angle));
    }

    [Fact]
    public void The_layout_box_swaps_its_sides_only_when_sideways()
    {
        Assert.Equal((30.0, 100.0), TextTurn.LayoutSize(90, 100, 30));
        Assert.Equal((30.0, 100.0), TextTurn.LayoutSize(270, 100, 30));
        Assert.Equal((100.0, 30.0), TextTurn.LayoutSize(180, 100, 30));
        Assert.Equal((100.0, 30.0), TextTurn.LayoutSize(30, 100, 30));
    }

    [Fact]
    public void A_new_item_is_upright_and_the_angle_survives_saving()
    {
        Assert.Equal(0, new ReportItem().TextRotation);

        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "x", TextRotation = 90 });

        var back = ReportDefinition.FromJson(def.ToJson());
        Assert.Equal(90, back.Detail.Items[0].TextRotation);
    }

    [Fact]
    public void An_old_file_without_the_setting_loads_upright()
    {
        var def = ReportDefinition.FromJson("""{ "Name": "Old", "Detail": { "Items": [ { "Kind": "Text", "Text": "x" } ] } }""");

        Assert.Equal(0, def.Detail.Items[0].TextRotation);
    }

    // ---- in a report ----

    private static ReportDefinition Definition(double rotation, bool canGrow)
    {
        var def = new ReportDefinition { Name = "Turned" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem
        {
            Kind = ItemKind.Text, Width = 50, Height = 20, CanGrow = canGrow, TextRotation = rotation,
            Text = "A fairly long piece of text that needs several lines in a narrow box"
        });
        return def;
    }

    private static RenderedItem Text(ReportDefinition def) =>
        ReportEngine.Run(def, null,
                new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 } },
                new ReportOptions { Culture = CultureInfo.InvariantCulture })
            .Pages[0].Items.First(i => i.Kind == ItemKind.Text);

    [Fact]
    public void The_angle_reaches_the_rendered_item()
    {
        Assert.Equal(270, Text(Definition(270, canGrow: false)).TextRotation);
    }

    [Fact]
    public void Upright_text_grows_but_turned_text_keeps_its_box()
    {
        Assert.True(Text(Definition(0, canGrow: true)).Height > 20, "upright text should grow");
        Assert.Equal(20, Text(Definition(90, canGrow: true)).Height);
    }
}
