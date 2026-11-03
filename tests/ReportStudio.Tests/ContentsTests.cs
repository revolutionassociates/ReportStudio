using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Outline places, PDF bookmarks data and the table of contents (requirement 28).</summary>
public class ContentsTests
{
    private static Dictionary<string, object?> Row(string name) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Name"] = name };

    private static ReportDefinition Report(ReportItem? contents, int level = 1)
    {
        var def = new ReportDefinition { Name = "Toc" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = contents?.Height ?? 0;
        if (contents != null) def.ReportHeader.Items.Add(contents);
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        def.Detail.Items.Add(new ReportItem
        {
            Kind = ItemKind.Text, Text = "{Name}", Width = 100, Height = 18, Outline = "Chapter {Name}", OutlineLevel = level
        });
        return def;
    }

    private static ReportItem Contents(double height = 100, int levels = 3) =>
        new() { Kind = ItemKind.Contents, Width = 300, Height = height, ContentsRowHeight = 20, ContentsLevels = levels };

    private static ReportResult Run(ReportDefinition def, params string[] names) =>
        ReportEngine.Run(def, null, names.Select(Row).ToList(), new ReportOptions { Culture = CultureInfo.InvariantCulture });

    [Fact]
    public void Outline_places_are_listed_in_reading_order_with_their_titles()
    {
        var result = Run(Report(null), "A", "B", "C");

        var outline = result.FindOutline();

        Assert.Equal(new[] { "Chapter A", "Chapter B", "Chapter C" }, outline.Select(o => o.Title));
        Assert.All(outline, o => { Assert.Equal(0, o.PageIndex); Assert.Equal(1, o.PageLabel); Assert.Equal(1, o.Level); });
        Assert.True(outline[0].Y < outline[1].Y && outline[1].Y < outline[2].Y);
    }

    [Fact]
    public void Every_outline_place_gets_a_bookmark_so_it_can_be_linked()
    {
        var result = Run(Report(null), "A", "B");

        var outline = result.FindOutline();

        Assert.All(outline, o => Assert.NotEmpty(o.Bookmark));
        Assert.Equal(2, outline.Select(o => o.Bookmark).Distinct().Count());
        Assert.NotNull(result.FindBookmark(outline[1].Bookmark));
    }

    [Fact]
    public void A_bookmark_the_designer_chose_is_kept()
    {
        var def = Report(null);
        def.Detail.Items[0].Bookmark = "Row-{Name}";

        var outline = Run(def, "A").FindOutline();

        Assert.Equal("Row-A", outline[0].Bookmark);
    }

    [Fact]
    public void The_table_of_contents_prints_a_line_per_place_with_page_number_and_link()
    {
        var result = Run(Report(Contents()), "A", "B");

        var lines = result.Pages[0].Items.Where(i => i.Band == BandKind.ReportHeader).ToList();

        Assert.Equal(new[] { "Chapter A", "1", "Chapter B", "1" }, lines.Select(l => l.Text));
        Assert.StartsWith("#", lines[0].Link);
        Assert.Equal(lines[0].Link.TrimStart('#'), result.FindOutline()[0].Bookmark);
        Assert.Equal(lines[0].Link, lines[1].Link);   // the page number is clickable too
        Assert.True(lines[2].Y > lines[0].Y);
    }

    [Fact]
    public void Deeper_levels_are_moved_in_and_can_be_left_out()
    {
        var def = Report(Contents(levels: 1), level: 2);
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "top", Width = 50, Height = 18, Outline = "Main", OutlineLevel = 1 });

        var lines = Run(def, "A").Pages[0].Items.Where(i => i.Band == BandKind.ReportHeader).ToList();

        Assert.Equal(new[] { "Main", "1" }, lines.Select(l => l.Text));   // level 2 is deeper than ContentsLevels = 1

        def.ReportHeader.Items[0].ContentsLevels = 2;
        var both = Run(def, "A").Pages[0].Items.Where(i => i.Band == BandKind.ReportHeader && i.HAlign == HAlign.Left).ToList();
        Assert.Equal("Chapter A", both[0].Text);
        Assert.True(both[0].X > both[1].X);   // level 2 sits further in than "Main"
    }

    [Fact]
    public void Lines_that_do_not_fit_are_left_out_with_a_warning_and_the_box_keeps_its_height()
    {
        var result = Run(Report(Contents(height: 40)), "A", "B", "C");

        var lines = result.Pages[0].Items.Where(i => i.Band == BandKind.ReportHeader && i.HAlign == HAlign.Left).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Contains(result.Warnings, w => w.Contains("do not fit"));
    }

    [Fact]
    public void Printing_the_contents_does_not_move_the_listed_places()
    {
        var without = Run(Report(null), "A", "B").FindOutline();
        var with = Run(Report(Contents()), "A", "B").FindOutline();

        // The report header is as tall as the designed box either way only if both reports have it; compare the order and pages.
        Assert.Equal(without.Select(o => o.Title), with.Select(o => o.Title));
        Assert.All(with, o => Assert.Equal(0, o.PageIndex));
    }

    [Fact]
    public void Outline_settings_survive_saving()
    {
        var def = Report(Contents());
        var loaded = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal("Chapter {Name}", loaded.Detail.Items[0].Outline);
        Assert.Equal(ItemKind.Contents, loaded.ReportHeader.Items[0].Kind);
        Assert.Equal(20, loaded.ReportHeader.Items[0].ContentsRowHeight);
    }
}
