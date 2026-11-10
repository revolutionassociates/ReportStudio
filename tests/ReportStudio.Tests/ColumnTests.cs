using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Multi-column pages (requirement 37, first part): the settings and the engine, in "down" and "across" order.</summary>
public class ColumnTests
{
    // Page 800 wide with 10 margins: 780 of content. Three columns with a gap of 30 are 240 wide each and start at 0, 270 and 540.
    private static ReportDefinition Definition(int columns, ColumnOrder order, double pageHeight = 200, double itemWidth = 100)
    {
        var def = new ReportDefinition { Name = "Columns" };
        def.DataSource.Provider = DbProvider.Host;
        def.Page.Width = 800; def.Page.Height = pageHeight;
        def.Page.MarginLeft = 10; def.Page.MarginRight = 10; def.Page.MarginTop = 0; def.Page.MarginBottom = 0;
        def.Page.Columns = columns; def.Page.ColumnGap = 30; def.Page.ColumnOrder = order;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 50;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{A}", Width = itemWidth, Height = 40 });
        return def;
    }

    private static ReportResult Run(ReportDefinition def, int rows) =>
        ReportEngine.Run(def, null,
            Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = i, ["G"] = "a" }),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static RenderedItem Cell(ReportResult result, int page, int number) =>
        result.Pages[page].Items.Single(i => i.Band == BandKind.Detail && i.Text == number.ToString(CultureInfo.InvariantCulture));

    private static (double X, double Y) At(RenderedItem item) => (item.X, item.Y);

    // ---- settings ----

    [Fact]
    public void A_page_has_one_column_unless_told_otherwise()
    {
        var page = new PageSettings();

        Assert.Equal(1, page.ColumnCount);
        Assert.Equal(page.ContentWidth, page.ColumnWidth);
        Assert.Equal(0, page.ColumnLeft(0));
    }

    [Fact]
    public void Columns_share_the_content_width_less_the_gaps()
    {
        var page = Definition(3, ColumnOrder.Down).Page;

        Assert.Equal(240, page.ColumnWidth);
        Assert.Equal(0, page.ColumnLeft(0));
        Assert.Equal(270, page.ColumnLeft(1));
        Assert.Equal(540, page.ColumnLeft(2));
    }

    [Fact]
    public void The_number_of_columns_is_kept_in_range()
    {
        Assert.Equal(1, new PageSettings { Columns = 0 }.ColumnCount);
        Assert.Equal(1, new PageSettings { Columns = -4 }.ColumnCount);
        Assert.Equal(PageSettings.MaxColumns, new PageSettings { Columns = 500 }.ColumnCount);
    }

    [Fact]
    public void A_negative_gap_counts_as_none()
    {
        var page = new PageSettings { Width = 800, MarginLeft = 0, MarginRight = 0, Columns = 2, ColumnGap = -50 };

        Assert.Equal(400, page.ColumnWidth);
    }

    // ---- down order ----

    [Fact]
    public void Down_order_fills_a_column_to_the_bottom_before_the_next_one_starts()
    {
        var result = Run(Definition(3, ColumnOrder.Down), rows: 10);       // 4 rows fit under each other

        var page = Assert.Single(result.Pages);
        Assert.Equal((10.0, 0.0), At(Cell(result, 0, 1)));
        Assert.Equal((10.0, 150.0), At(Cell(result, 0, 4)));
        Assert.Equal((280.0, 0.0), At(Cell(result, 0, 5)));
        Assert.Equal((280.0, 150.0), At(Cell(result, 0, 8)));
        Assert.Equal((550.0, 0.0), At(Cell(result, 0, 9)));
        Assert.Equal(10, page.Items.Count(i => i.Band == BandKind.Detail));
    }

    [Fact]
    public void Down_order_starts_a_new_page_when_the_last_column_is_full()
    {
        var result = Run(Definition(3, ColumnOrder.Down), rows: 15);       // 12 per page

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(12, result.Pages[0].Items.Count(i => i.Band == BandKind.Detail));
        Assert.Equal((10.0, 0.0), At(Cell(result, 1, 13)));
        Assert.Equal((10.0, 100.0), At(Cell(result, 1, 15)));
    }

    [Fact]
    public void One_column_is_laid_out_as_before()
    {
        var result = Run(Definition(1, ColumnOrder.Down), rows: 6);

        Assert.Equal(2, result.Pages.Count);
        Assert.All(result.Pages.SelectMany(p => p.Items), i => Assert.Equal(10, i.X));
        Assert.Equal(4, result.Pages[0].Items.Count);
    }

    [Fact]
    public void A_group_footer_goes_full_width_below_the_longest_column()
    {
        var def = Definition(3, ColumnOrder.Down, pageHeight: 600);
        var group = new GroupDefinition { Expression = "G" };
        group.Header.Height = 20;
        group.Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "Head", Width = 100, Height = 18 });
        group.Footer.Height = 20;
        group.Footer.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "Foot", Width = 100, Height = 18 });
        def.Groups.Add(group);

        var result = Run(def, rows: 14);                                  // 11 rows under the 20 px header, then 3 in the next column

        var page = Assert.Single(result.Pages);
        Assert.Equal((10.0, 0.0), At(page.Items.Single(i => i.Text == "Head")));
        Assert.Equal(20, Cell(result, 0, 1).Y);
        Assert.Equal((280.0, 20.0), At(Cell(result, 0, 12)));
        Assert.Equal((10.0, 570.0), At(page.Items.Single(i => i.Text == "Foot")));
    }

    // ---- across order ----

    [Fact]
    public void Across_order_goes_left_to_right_and_then_to_the_next_line()
    {
        var result = Run(Definition(3, ColumnOrder.Across), rows: 7);

        Assert.Equal((10.0, 0.0), At(Cell(result, 0, 1)));
        Assert.Equal((280.0, 0.0), At(Cell(result, 0, 2)));
        Assert.Equal((550.0, 0.0), At(Cell(result, 0, 3)));
        Assert.Equal((10.0, 50.0), At(Cell(result, 0, 4)));
        Assert.Equal((10.0, 100.0), At(Cell(result, 0, 7)));
    }

    [Fact]
    public void Across_order_starts_a_new_page_when_the_lines_are_used_up()
    {
        var result = Run(Definition(3, ColumnOrder.Across), rows: 13);     // 4 lines of 3 per page

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(12, result.Pages[0].Items.Count(i => i.Band == BandKind.Detail));
        var last = Assert.Single(result.Pages[1].Items);
        Assert.Equal((10.0, 0.0), At(last));
        Assert.Equal("13", last.Text);
    }

    [Fact]
    public void Across_order_keeps_the_row_numbers_in_step()
    {
        var def = Definition(2, ColumnOrder.Across);
        def.Detail.Items[0].Text = "{RowNumber}";

        var result = Run(def, rows: 5);

        Assert.Equal(new[] { "1", "2", "3", "4", "5" },
            result.Pages.SelectMany(p => p.Items).OrderBy(i => i.Y).ThenBy(i => i.X).Select(i => i.Text).ToArray());
    }

    // ---- warnings ----

    [Fact]
    public void An_element_wider_than_a_column_is_reported()
    {
        var result = Run(Definition(3, ColumnOrder.Down, itemWidth: 300), rows: 1);

        Assert.Contains(result.Warnings, w => w.Contains("wider than a column"));
    }

    [Fact]
    public void Elements_that_fit_a_column_give_no_warning()
    {
        var result = Run(Definition(3, ColumnOrder.Down, itemWidth: 240), rows: 1);

        Assert.Empty(result.Warnings);
    }
}
