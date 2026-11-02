using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Can-shrink and suppress-if-blank, for the lines of an address block (requirement 22).</summary>
public class ShrinkTests
{
    private static ReportDefinition Report(double detailHeight = 60)
    {
        var def = new ReportDefinition { Name = "Shrink" };
        def.DataSource.Provider = DbProvider.Host;
        def.Page.MarginLeft = 0;
        def.Page.MarginTop = 0;
        def.Page.MarginBottom = 0;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = detailHeight;
        return def;
    }

    private static ReportItem Line(string text, double y, double x = 0, double width = 100, Action<ReportItem>? configure = null)
    {
        var item = new ReportItem { Kind = ItemKind.Text, Text = text, X = x, Y = y, Width = width, Height = 20, WordWrap = true };
        configure?.Invoke(item);
        return item;
    }

    private static Dictionary<string, object?> Row(string b) =>
        new(StringComparer.OrdinalIgnoreCase) { ["B"] = b };

    private static List<RenderedItem> Run(ReportDefinition def, params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture })
            .Pages.SelectMany(p => p.Items).ToList();

    private static double YOf(List<RenderedItem> items, string text) => items.Single(i => i.Text == text).Y;

    private static void Block(ReportDefinition def, Action<ReportItem> middle)
    {
        def.Detail.Items.Add(Line("one", 0));
        def.Detail.Items.Add(Line("{B}", 20, configure: middle));
        def.Detail.Items.Add(Line("three", 40));
    }

    [Fact]
    public void A_blank_line_with_suppress_is_not_printed_but_leaves_its_space()
    {
        var def = Report();
        Block(def, i => i.SuppressIfBlank = true);

        var items = Run(def, Row(""));

        Assert.Equal(new[] { "one", "three" }, items.Select(i => i.Text));
        Assert.Equal(40, YOf(items, "three"));
    }

    [Fact]
    public void A_blank_line_with_can_shrink_closes_the_gap()
    {
        var def = Report();
        Block(def, i => i.CanShrink = true);

        var items = Run(def, Row("  "));            // spaces count as blank

        Assert.Equal(new[] { "one", "three" }, items.Select(i => i.Text));
        Assert.Equal(20, YOf(items, "three"));
    }

    [Fact]
    public void A_line_with_text_keeps_its_place_and_height()
    {
        var def = Report();
        Block(def, i => { i.CanShrink = true; i.SuppressIfBlank = true; });

        var items = Run(def, Row("two"));

        Assert.Equal(40, YOf(items, "three"));
        Assert.Equal(20, items.Single(i => i.Text == "two").Height);
    }

    [Fact]
    public void Blank_lines_one_above_the_other_add_up()
    {
        var def = Report(80);
        def.Detail.Items.Add(Line("one", 0));
        def.Detail.Items.Add(Line("{B}", 20, configure: i => i.CanShrink = true));
        def.Detail.Items.Add(Line("{B}", 40, configure: i => i.CanShrink = true));
        def.Detail.Items.Add(Line("four", 60));

        var items = Run(def, Row(""));

        Assert.Equal(20, YOf(items, "four"));
    }

    [Fact]
    public void Blank_lines_side_by_side_count_once_for_a_line_below_both()
    {
        var def = Report();
        def.Detail.Items.Add(Line("{B}", 20, x: 0, configure: i => i.CanShrink = true));
        def.Detail.Items.Add(Line("{B}", 20, x: 110, configure: i => i.CanShrink = true));
        def.Detail.Items.Add(Line("wide", 40, x: 0, width: 210));

        var items = Run(def, Row(""));

        Assert.Equal(20, YOf(items, "wide"));         // moved up once, not twice
    }

    [Fact]
    public void A_line_beside_the_blank_one_does_not_move()
    {
        var def = Report();
        def.Detail.Items.Add(Line("{B}", 20, x: 0, configure: i => i.CanShrink = true));
        def.Detail.Items.Add(Line("right", 40, x: 110));
        def.Detail.Items.Add(Line("left", 40, x: 0));

        var items = Run(def, Row(""));

        Assert.Equal(40, YOf(items, "right"));
        Assert.Equal(20, YOf(items, "left"));
    }

    [Fact]
    public void A_band_that_can_shrink_is_only_as_tall_as_what_prints()
    {
        var def = Report();
        def.Detail.CanShrink = true;
        Block(def, i => i.CanShrink = true);

        var items = Run(def, Row(""), Row("two"));

        // Row 1 is two lines tall (40); row 2 starts right after it.
        Assert.Equal(40, items.First(i => i.Text == "one" && i.Y > 0).Y);
    }

    [Fact]
    public void Without_can_shrink_the_band_keeps_its_designed_height()
    {
        var def = Report();
        Block(def, i => i.CanShrink = true);

        var items = Run(def, Row(""), Row("two"));

        Assert.Equal(60, items.First(i => i.Text == "one" && i.Y > 0).Y);
    }

    [Fact]
    public void A_band_with_nothing_to_print_disappears()
    {
        var def = Report();
        def.Detail.SuppressIfBlank = true;
        def.Detail.Items.Add(Line("{B}", 0, configure: i => i.SuppressIfBlank = true));
        def.Detail.Items.Add(Line("{B}", 20, configure: i => i.SuppressIfBlank = true));

        var items = Run(def, Row(""), Row("x"));

        // The first row takes no room: the second row's lines start at the top.
        Assert.Equal(0, items.First(i => i.Text == "x").Y);
    }

    [Fact]
    public void A_band_with_fixed_content_is_never_blank()
    {
        var def = Report();
        def.Detail.SuppressIfBlank = true;
        def.Detail.Items.Add(Line("label", 0));
        def.Detail.Items.Add(Line("{B}", 20, configure: i => i.SuppressIfBlank = true));

        var items = Run(def, Row(""), Row(""));

        Assert.Equal(2, items.Count(i => i.Text == "label"));
    }

    [Fact]
    public void A_blank_picture_can_shrink_too()
    {
        var def = Report();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Image, X = 0, Y = 0, Width = 100, Height = 40, CanShrink = true });
        def.Detail.Items.Add(Line("below", 40));

        var items = Run(def, Row(""));

        Assert.Equal(0, YOf(items, "below"));
        Assert.DoesNotContain(items, i => i.Kind == ItemKind.Image);
    }

    [Fact]
    public void The_settings_survive_saving_and_loading()
    {
        var def = Report();
        def.Detail.SuppressIfBlank = true;
        def.Detail.CanShrink = true;
        def.Detail.Items.Add(Line("x", 0, configure: i => { i.SuppressIfBlank = true; i.CanShrink = true; }));

        var copy = def.Clone();

        Assert.True(copy.Detail.SuppressIfBlank);
        Assert.True(copy.Detail.CanShrink);
        Assert.True(copy.Detail.Items[0].SuppressIfBlank);
        Assert.True(copy.Detail.Items[0].CanShrink);
    }
}
