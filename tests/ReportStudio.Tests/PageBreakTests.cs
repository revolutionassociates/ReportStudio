using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Keep-together, orphan and widow control, and repeating a group header (requirement 21).</summary>
public class PageBreakTests
{
    // A small page: the body runs from y = 10 to y = 80, so seven 10-high bands fit on a page.
    private static ReportDefinition Report(Action<GroupDefinition> configure)
    {
        var def = new ReportDefinition { Name = "Breaks" };
        def.DataSource.Provider = DbProvider.Host;
        def.Page.Height = 120;
        def.Page.MarginTop = 10;
        def.Page.MarginBottom = 10;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0;
        def.PageFooter.Height = 30;
        def.Detail.Height = 10;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Amount}", Width = 100, Height = 8 });

        var group = new GroupDefinition { Expression = "Dept" };
        group.Header.Height = 10;
        group.Footer.Height = 10;
        group.Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Dept + IIf(Continued, ' (cont)', '')}", Width = 100, Height = 8 });
        group.Footer.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "F{Dept}", Width = 100, Height = 8 });
        configure(group);
        def.Groups.Add(group);
        return def;
    }

    private static Dictionary<string, object?> Row(string dept, decimal amount) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Dept"] = dept, ["Amount"] = amount };

    private static ReportResult Run(ReportDefinition def, params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

    /// <summary>The 1-based page of the first printed item of that band with that text, or 0.</summary>
    private static int PageOf(ReportResult result, BandKind band, string text)
    {
        foreach (var page in result.Pages)
            if (page.Items.Any(i => i.Band == band && i.Text == text)) return page.Number;
        return 0;
    }

    private static List<string> HeaderTexts(ReportResult result) =>
        result.Pages.SelectMany(p => p.Items).Where(i => i.Band == BandKind.GroupHeader).Select(i => i.Text).ToList();

    /// <summary>Rows numbered 1..count, or first..first+count-1 so a row can be told apart from another group's.</summary>
    private static Dictionary<string, object?>[] Rows(string dept, int count, int first = 1) =>
        Enumerable.Range(first, count).Select(n => Row(dept, n)).ToArray();

    [Fact]
    public void Without_keep_together_a_group_is_split_across_pages()
    {
        var def = Report(_ => { });

        var result = Run(def, Rows("A", 3).Concat(Rows("B", 3, 11)).ToArray());

        Assert.Equal(1, PageOf(result, BandKind.GroupHeader, "B"));
        Assert.Equal(2, PageOf(result, BandKind.GroupFooter, "FB"));       // B starts on page 1 and ends on page 2
    }

    [Fact]
    public void Keep_together_moves_a_group_that_would_be_split_to_the_next_page()
    {
        var def = Report(g => g.KeepTogether = true);

        var result = Run(def, Rows("A", 3).Concat(Rows("B", 3, 11)).ToArray());

        Assert.Equal(1, PageOf(result, BandKind.GroupHeader, "A"));
        Assert.Equal(2, PageOf(result, BandKind.GroupHeader, "B"));
        Assert.Equal(2, PageOf(result, BandKind.GroupFooter, "FB"));
    }

    [Fact]
    public void A_group_taller_than_a_page_is_not_moved()
    {
        var def = Report(g => g.KeepTogether = true);

        var result = Run(def, Rows("A", 1).Concat(Rows("B", 8, 11)).ToArray());      // B needs 100, a page holds 70

        Assert.Equal(1, PageOf(result, BandKind.GroupHeader, "B"));
    }

    [Fact]
    public void A_header_alone_at_the_bottom_of_a_page_is_the_default_behaviour()
    {
        var def = Report(_ => { });

        var result = Run(def, Rows("A", 4).Concat(Rows("B", 2, 11)).ToArray());      // A ends exactly at y = 70

        Assert.Equal(1, PageOf(result, BandKind.GroupHeader, "B"));
        Assert.Equal(2, PageOf(result, BandKind.Detail, "11"));                       // B's first row is already on page 2
    }

    [Fact]
    public void Keep_header_with_rows_moves_a_lonely_header_to_the_next_page()
    {
        var def = Report(g => g.KeepHeaderWithRows = 1);

        var result = Run(def, Rows("A", 4).Concat(Rows("B", 2, 11)).ToArray());

        Assert.Equal(2, PageOf(result, BandKind.GroupHeader, "B"));
        Assert.Equal(2, PageOf(result, BandKind.Detail, "11"));
    }

    [Fact]
    public void A_footer_alone_at_the_top_of_a_page_is_the_default_behaviour()
    {
        var def = Report(_ => { });

        var result = Run(def, Rows("A", 6));

        Assert.Equal(1, PageOf(result, BandKind.Detail, "6"));
        Assert.Equal(2, PageOf(result, BandKind.GroupFooter, "FA"));
    }

    [Fact]
    public void Keep_footer_with_rows_takes_the_last_row_along_to_the_next_page()
    {
        var def = Report(g => g.KeepFooterWithRows = 1);

        var result = Run(def, Rows("A", 6));

        Assert.Equal(2, PageOf(result, BandKind.Detail, "6"));
        Assert.Equal(2, PageOf(result, BandKind.GroupFooter, "FA"));
        Assert.Equal(1, PageOf(result, BandKind.Detail, "5"));
    }

    [Fact]
    public void Repeat_header_prints_the_header_again_on_each_continuation_page()
    {
        var def = Report(g => g.RepeatHeader = true);

        var result = Run(def, Rows("A", 8));

        Assert.Equal(new[] { "A", "A (cont)" }, HeaderTexts(result));
        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public void Without_repeat_header_a_continued_group_has_one_header()
    {
        var result = Run(Report(_ => { }), Rows("A", 8));

        Assert.Equal(new[] { "A" }, HeaderTexts(result));
    }

    [Fact]
    public void A_group_that_starts_on_a_new_page_does_not_repeat_its_own_header()
    {
        var def = Report(g => { g.RepeatHeader = true; g.StartNewPage = true; });

        var result = Run(def, Rows("A", 2).Concat(Rows("B", 2, 11)).ToArray());

        Assert.Equal(new[] { "A", "B" }, HeaderTexts(result));
    }

    [Fact]
    public void Negative_settings_are_clamped_and_all_settings_survive_json()
    {
        var def = Report(g => { g.KeepHeaderWithRows = -3; g.KeepFooterWithRows = 2; g.KeepTogether = true; g.RepeatHeader = true; });

        var group = def.Clone().Groups[0];

        Assert.Equal(0, group.KeepHeaderWithRows);
        Assert.Equal(2, group.KeepFooterWithRows);
        Assert.True(group.KeepTogether);
        Assert.True(group.RepeatHeader);
    }
}
