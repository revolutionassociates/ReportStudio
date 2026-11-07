using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Gradient fill (requirement 34, second part).</summary>
public class GradientTests
{
    private static RenderedItem Item(ItemKind kind, string back = "#FF0000", string second = "#0000FF") =>
        new() { Kind = kind, Width = 100, Height = 40, BackColor = back, GradientColor = second };

    // ---- when an item has a gradient ----

    [Theory]
    [InlineData(ItemKind.Rectangle)]
    [InlineData(ItemKind.Ellipse)]
    [InlineData(ItemKind.Text)]
    public void Boxes_ellipses_and_text_boxes_can_have_a_gradient(ItemKind kind)
    {
        Assert.True(ShapeGeometry.UsesGradient(Item(kind)));
    }

    [Theory]
    [InlineData(ItemKind.Line)]
    [InlineData(ItemKind.Image)]
    [InlineData(ItemKind.Barcode)]
    [InlineData(ItemKind.QrCode)]
    [InlineData(ItemKind.Barcode2D)]
    public void Other_elements_keep_a_solid_fill(ItemKind kind)
    {
        Assert.False(ShapeGeometry.UsesGradient(Item(kind)));
    }

    [Fact]
    public void Both_colours_are_needed()
    {
        Assert.False(ShapeGeometry.UsesGradient(Item(ItemKind.Rectangle, second: "")));
        Assert.False(ShapeGeometry.UsesGradient(Item(ItemKind.Rectangle, back: "")));
        Assert.False(ShapeGeometry.UsesGradient(Item(ItemKind.Rectangle, second: "   ")));
    }

    // ---- in a report ----

    private static ReportResult Run(params ReportItem[] items)
    {
        var def = new ReportDefinition { Name = "Gradients" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 60;
        foreach (var item in items) def.Detail.Items.Add(item);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 };
        return ReportEngine.Run(def, null, new[] { row }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    [Fact]
    public void The_gradient_settings_reach_the_page()
    {
        var result = Run(new ReportItem
        {
            Kind = ItemKind.Rectangle, Width = 100, Height = 40, BackColor = "#1F2937",
            GradientColor = "#6B7280", GradientDirection = GradientDirection.LeftToRight
        });

        var box = Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.Rectangle);
        Assert.Equal("#6B7280", box.GradientColor);
        Assert.Equal(GradientDirection.LeftToRight, box.GradientDirection);
        Assert.True(ShapeGeometry.UsesGradient(box));
    }

    [Fact]
    public void A_conditional_format_can_change_the_back_colour_and_the_gradient_follows()
    {
        var item = new ReportItem
        {
            Kind = ItemKind.Rectangle, Width = 100, Height = 40, BackColor = "#00FF00", GradientColor = "#FFFFFF"
        };
        item.Rules.Add(new FormatRule { Condition = "A > 0", BackColor = "#FF0000" });

        var box = Assert.Single(Run(item).Pages[0].Items, i => i.Kind == ItemKind.Rectangle);

        Assert.Equal("#FF0000", box.BackColor);
        Assert.Equal("#FFFFFF", box.GradientColor);
    }

    // ---- saving and copying ----

    [Fact]
    public void The_settings_survive_saving()
    {
        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem
        {
            Kind = ItemKind.Text, BackColor = "#112233", GradientColor = "#AABBCC", GradientDirection = GradientDirection.LeftToRight
        });

        var back = ReportDefinition.FromJson(def.ToJson()).Detail.Items[0];

        Assert.Equal("#AABBCC", back.GradientColor);
        Assert.Equal(GradientDirection.LeftToRight, back.GradientDirection);
    }

    [Fact]
    public void An_item_starts_with_a_solid_fill()
    {
        var item = new ReportItem();

        Assert.Equal("", item.GradientColor);
        Assert.Equal(GradientDirection.TopToBottom, item.GradientDirection);
    }

    [Fact]
    public void Copy_appearance_carries_the_gradient()
    {
        var from = new ReportItem { GradientColor = "#123456", GradientDirection = GradientDirection.LeftToRight };
        var to = new ReportItem();

        ItemArrange.CopyAppearance(from, to);

        Assert.Equal("#123456", to.GradientColor);
        Assert.Equal(GradientDirection.LeftToRight, to.GradientDirection);
    }
}
