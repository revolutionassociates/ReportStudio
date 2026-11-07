using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Ellipse and rounded box (requirement 34, first part).</summary>
public class ShapeTests
{
    // ---- the radius that can really be drawn ----

    [Theory]
    [InlineData(10, 100, 40, 10)]      // fits: kept
    [InlineData(30, 100, 40, 20)]      // more than half of the shorter side: cut back
    [InlineData(999, 100, 40, 20)]     // fully round ends
    [InlineData(999, 40, 100, 20)]     // the same for a tall box
    [InlineData(0, 100, 40, 0)]
    [InlineData(-5, 100, 40, 0)]       // negative: square corners
    [InlineData(10, 0, 40, 0)]         // no area, nothing to round
    [InlineData(10, 100, 0, 0)]
    public void The_radius_never_exceeds_half_the_shorter_side(double radius, double width, double height, double expected)
    {
        Assert.Equal(expected, ShapeGeometry.CornerRadius(radius, width, height));
    }

    [Fact]
    public void A_radius_that_is_not_a_number_gives_square_corners()
    {
        Assert.Equal(0, ShapeGeometry.CornerRadius(double.NaN, 100, 40));
        Assert.Equal(0, ShapeGeometry.CornerRadius(10, double.NaN, 40));
    }

    // ---- in a report ----

    private static ReportResult Run(params ReportItem[] items)
    {
        var def = new ReportDefinition { Name = "Shapes" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 60;
        foreach (var item in items) def.Detail.Items.Add(item);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 };
        return ReportEngine.Run(def, null, new[] { row }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    [Fact]
    public void An_ellipse_and_a_rounded_box_reach_the_page()
    {
        var result = Run(
            new ReportItem { Kind = ItemKind.Ellipse, Width = 100, Height = 50, BackColor = "#FFE0B2", BorderWidth = 1, BorderColor = "#555555" },
            new ReportItem { Kind = ItemKind.Rectangle, X = 120, Width = 100, Height = 50, CornerRadius = 12, BorderWidth = 1 });

        var items = result.Pages[0].Items;
        var ellipse = Assert.Single(items, i => i.Kind == ItemKind.Ellipse);
        var box = Assert.Single(items, i => i.Kind == ItemKind.Rectangle);

        Assert.Equal("#FFE0B2", ellipse.BackColor);
        Assert.Equal(12, box.CornerRadius);
        Assert.Equal(0, ellipse.CornerRadius);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_shape_with_a_false_condition_is_not_printed()
    {
        var result = Run(new ReportItem { Kind = ItemKind.Ellipse, Width = 50, Height = 50, VisibleWhen = "A > 5" });

        Assert.DoesNotContain(result.Pages[0].Items, i => i.Kind == ItemKind.Ellipse);
    }

    // ---- saving and copying ----

    [Fact]
    public void The_shape_settings_survive_saving()
    {
        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Ellipse, Width = 80, Height = 40 });
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Rectangle, CornerRadius = 8.5 });

        var back = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal(ItemKind.Ellipse, back.Detail.Items[0].Kind);
        Assert.Equal(8.5, back.Detail.Items[1].CornerRadius);
    }

    [Fact]
    public void A_new_box_has_square_corners()
    {
        Assert.Equal(0, new ReportItem { Kind = ItemKind.Rectangle }.CornerRadius);
    }

    [Fact]
    public void Copy_appearance_carries_the_corner_radius()
    {
        var from = new ReportItem { Kind = ItemKind.Rectangle, CornerRadius = 14 };
        var to = new ReportItem { Kind = ItemKind.Rectangle };

        ItemArrange.CopyAppearance(from, to);

        Assert.Equal(14, to.CornerRadius);
    }

    [Fact]
    public void Cloning_keeps_the_corner_radius()
    {
        Assert.Equal(6, new ReportItem { CornerRadius = 6 }.Clone().CornerRadius);
    }
}
