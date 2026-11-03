using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Stacking order, grouping, locking, the format painter and alignment guides of the designer (requirement 25).</summary>
public class ItemArrangeTests
{
    private static ReportItem Item(string name, double x = 0, double y = 0, double w = 100, double h = 20) =>
        new() { Name = name, Kind = ItemKind.Text, Text = name, X = x, Y = y, Width = w, Height = h };

    private static Band BandOf(params ReportItem[] items)
    {
        var band = new Band();
        band.Items.AddRange(items);
        return band;
    }

    private static string Order(Band band) => string.Join("", band.Items.Select(i => i.Name));

    // ---- stacking order ----

    [Fact]
    public void Bring_to_front_puts_the_items_last_in_their_old_order()
    {
        var (a, b, c, d) = (Item("A"), Item("B"), Item("C"), Item("D"));
        var band = BandOf(a, b, c, d);

        ItemArrange.BringToFront(band, new[] { c, a });

        Assert.Equal("BDAC", Order(band));
    }

    [Fact]
    public void Send_to_back_puts_the_items_first_in_their_old_order()
    {
        var (a, b, c, d) = (Item("A"), Item("B"), Item("C"), Item("D"));
        var band = BandOf(a, b, c, d);

        ItemArrange.SendToBack(band, new[] { d, b });

        Assert.Equal("BDAC", Order(band));
    }

    [Fact]
    public void Bring_forward_moves_a_block_up_one_place()
    {
        var (a, b, c, d) = (Item("A"), Item("B"), Item("C"), Item("D"));
        var band = BandOf(a, b, c, d);

        ItemArrange.BringForward(band, new[] { a, b });

        Assert.Equal("CABD", Order(band));
    }

    [Fact]
    public void Send_backward_moves_a_block_down_one_place()
    {
        var (a, b, c, d) = (Item("A"), Item("B"), Item("C"), Item("D"));
        var band = BandOf(a, b, c, d);

        ItemArrange.SendBackward(band, new[] { c, d });

        Assert.Equal("ACDB", Order(band));
    }

    [Fact]
    public void An_item_already_at_the_end_stays_when_moved_that_way()
    {
        var (a, b) = (Item("A"), Item("B"));
        var band = BandOf(a, b);

        ItemArrange.BringForward(band, new[] { b });
        ItemArrange.SendBackward(band, new[] { a });

        Assert.Equal("AB", Order(band));
    }

    // ---- grouping ----

    [Fact]
    public void A_group_needs_two_items_and_gives_them_one_id()
    {
        var (a, b) = (Item("A"), Item("B"));

        Assert.False(ItemArrange.Group(new[] { a }));
        Assert.True(ItemArrange.Group(new[] { a, b }));

        Assert.NotEmpty(a.ItemGroup);
        Assert.Equal(a.ItemGroup, b.ItemGroup);
    }

    [Fact]
    public void Selecting_one_member_selects_the_whole_group_even_in_another_band()
    {
        var (a, b, c, outsider) = (Item("A"), Item("B"), Item("C"), Item("X"));
        ItemArrange.Group(new[] { a, b, c });
        var first = BandOf(a, b, outsider);
        var second = BandOf(c);

        var selection = ItemArrange.ExpandGroups(new[] { first, second }, new[] { b });

        Assert.Equal(new[] { "B", "A", "C" }.OrderBy(x => x), selection.Select(i => i.Name).OrderBy(x => x));
        Assert.DoesNotContain(outsider, selection);
    }

    [Fact]
    public void Ungrouping_frees_the_items()
    {
        var (a, b) = (Item("A"), Item("B"));
        ItemArrange.Group(new[] { a, b });

        ItemArrange.Ungroup(new[] { a, b });

        Assert.Equal("", a.ItemGroup);
        Assert.Single(ItemArrange.ExpandGroups(new[] { BandOf(a, b) }, new[] { a }));
    }

    [Fact]
    public void Items_that_are_not_grouped_expand_to_themselves()
    {
        var (a, b) = (Item("A"), Item("B"));

        var selection = ItemArrange.ExpandGroups(new[] { BandOf(a, b) }, new[] { a });

        Assert.Equal(new[] { a }, selection);
    }

    // ---- locking and saving ----

    [Fact]
    public void Locked_and_group_survive_saving_and_copying()
    {
        var def = new ReportDefinition { Name = "Arrange" };
        var a = Item("A");
        a.Locked = true;
        a.ItemGroup = "abc12345";
        def.Detail.Items.Add(a);

        var loaded = ReportDefinition.FromJson(def.ToJson());
        var copy = def.Clone();

        Assert.True(loaded.Detail.Items[0].Locked);
        Assert.Equal("abc12345", loaded.Detail.Items[0].ItemGroup);
        Assert.True(copy.Detail.Items[0].Locked);
        Assert.Equal("abc12345", copy.Detail.Items[0].ItemGroup);
    }

    [Fact]
    public void A_report_saved_before_these_settings_loads_unlocked_and_ungrouped()
    {
        var def = new ReportDefinition { Name = "Old" };
        def.Detail.Items.Add(Item("A"));
        var json = def.ToJson();
        json = System.Text.RegularExpressions.Regex.Replace(json, "\"locked\"\\s*:\\s*false\\s*,?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        json = System.Text.RegularExpressions.Regex.Replace(json, "\"itemGroup\"\\s*:\\s*\"\"\\s*,?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.DoesNotContain("ocked", json);          // the settings really are gone from the old-style file

        var loaded = ReportDefinition.FromJson(json);

        Assert.False(loaded.Detail.Items[0].Locked);
        Assert.Equal("", loaded.Detail.Items[0].ItemGroup);
    }

    // ---- format painter ----

    [Fact]
    public void The_format_painter_copies_the_look_but_not_position_text_or_rules()
    {
        var from = Item("Source", x: 10, y: 20);
        from.FontFamily = "Ubuntu"; from.FontSize = 14; from.Bold = true; from.Italic = true; from.Underline = true;
        from.HAlign = HAlign.Right; from.VAlign = VAlign.Bottom; from.WordWrap = false;
        from.ForeColor = "#112233"; from.BackColor = "#EEEEEE"; from.BorderColor = "#445566"; from.BorderWidth = 2;
        from.Format = "N2";

        var to = Item("Target", x: 300, y: 40, w: 50);
        to.Rules.Add(new FormatRule { Condition = "Net < 0", ForeColor = "#FF0000" });

        ItemArrange.CopyAppearance(from, to);

        Assert.Equal(14, to.FontSize);
        Assert.True(to.Bold && to.Italic && to.Underline);
        Assert.Equal(HAlign.Right, to.HAlign);
        Assert.Equal(VAlign.Bottom, to.VAlign);
        Assert.False(to.WordWrap);
        Assert.Equal("#112233", to.ForeColor);
        Assert.Equal("#EEEEEE", to.BackColor);
        Assert.Equal("#445566", to.BorderColor);
        Assert.Equal(2, to.BorderWidth);
        Assert.Equal("N2", to.Format);

        Assert.Equal("Target", to.Text);
        Assert.Equal(300, to.X);
        Assert.Equal(40, to.Y);
        Assert.Equal(50, to.Width);
        Assert.Single(to.Rules);
    }

    // ---- alignment guides ----

    [Fact]
    public void A_box_close_to_a_left_edge_snaps_to_it_and_shows_a_guide()
    {
        var moving = new Box(98, 0, 50, 20);
        var targets = new[] { new Box(100, 50, 80, 20) };

        var (dx, _, guides) = ItemArrange.SnapToGuides(moving, targets, Array.Empty<Box>(), tolerance: 4);

        Assert.Equal(2, dx);
        Assert.Contains(new Guide(true, 100), guides);
    }

    [Fact]
    public void A_box_far_from_everything_does_not_move_and_shows_no_guide()
    {
        var moving = new Box(90, 0, 50, 20);
        var targets = new[] { new Box(200, 50, 80, 20) };

        var (dx, dy, guides) = ItemArrange.SnapToGuides(moving, targets, targets, tolerance: 4);

        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
        Assert.Empty(guides);
    }

    [Fact]
    public void Centres_line_up_too()
    {
        var moving = new Box(100, 0, 100, 20);       // centre 150
        var targets = new[] { new Box(120, 50, 64, 20) };   // centre 152

        var (dx, _, guides) = ItemArrange.SnapToGuides(moving, targets, Array.Empty<Box>(), 4);

        Assert.Equal(2, dx);
        Assert.Contains(new Guide(true, 152), guides);
    }

    [Fact]
    public void The_nearest_edge_wins_and_horizontal_guides_use_the_vertical_targets()
    {
        var moving = new Box(0, 47, 40, 20);                          // top 47, centre 57, bottom 67
        var yTargets = new[] { new Box(500, 50, 10, 200), new Box(500, 45, 10, 200) };   // lines 50/150/250 and 45/145/245

        var (_, dy, guides) = ItemArrange.SnapToGuides(moving, Array.Empty<Box>(), yTargets, 4);

        Assert.Equal(-2, dy);                                          // 47 -> 45 (distance 2) beats 47 -> 50 (distance 3)
        Assert.Contains(new Guide(false, 45), guides);
        Assert.DoesNotContain(new Guide(false, 50), guides);
    }

    [Fact]
    public void Several_matching_lines_each_get_a_guide()
    {
        var moving = new Box(100, 0, 50, 20);
        var targets = new[] { new Box(100, 50, 50, 20) };      // same left and same right

        var (dx, _, guides) = ItemArrange.SnapToGuides(moving, targets, Array.Empty<Box>(), 4);

        Assert.Equal(0, dx);
        Assert.Contains(new Guide(true, 100), guides);
        Assert.Contains(new Guide(true, 150), guides);
        Assert.Contains(new Guide(true, 125), guides);
    }
}
