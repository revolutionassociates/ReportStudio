using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Sorting groups by their total, and Top-N (requirement 19).</summary>
public class GroupOrderTests
{
    private static Dictionary<string, object?> Row(string dept, decimal amount, string customer = "x") =>
        new(StringComparer.OrdinalIgnoreCase) { ["Dept"] = dept, ["Cust"] = customer, ["Amount"] = amount };

    private static Dictionary<string, object?>[] Sample() => new[]
    {
        Row("A", 10), Row("A", 20), Row("B", 50), Row("C", 5), Row("C", 5), Row("D", 40)
    };

    private static ReportDefinition Report(string sortExpression, bool descending, int topN)
    {
        var def = new ReportDefinition { Name = "Top" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        def.ReportFooter.Height = 20;
        def.Groups.Add(new GroupDefinition { Expression = "Dept", SortExpression = sortExpression, SortDescending = descending, TopN = topN });
        def.Groups[0].Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Dept}", Width = 100, Height = 18 });
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Amount}", Width = 100, Height = 18 });
        def.ReportFooter.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Sum(Amount)}/{Count()}", Width = 100, Height = 18 });
        return def;
    }

    private static ReportResult Run(ReportDefinition def, params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static List<string> Texts(ReportResult result, BandKind band) =>
        result.Pages.SelectMany(p => p.Items).Where(i => i.Band == band).Select(i => i.Text).ToList();

    [Fact]
    public void Groups_can_be_ordered_by_their_total_largest_first()
    {
        var result = Run(Report("Sum(Amount)", descending: true, topN: 0), Sample());

        Assert.Equal(new[] { "B", "D", "A", "C" }, Texts(result, BandKind.GroupHeader));
    }

    [Fact]
    public void Groups_can_be_ordered_smallest_first()
    {
        var result = Run(Report("Sum(Amount)", descending: false, topN: 0), Sample());

        Assert.Equal(new[] { "C", "A", "D", "B" }, Texts(result, BandKind.GroupHeader));
    }

    [Fact]
    public void Top_n_keeps_only_the_first_groups_and_the_totals_follow()
    {
        var result = Run(Report("Sum(Amount)", descending: true, topN: 2), Sample());

        Assert.Equal(new[] { "B", "D" }, Texts(result, BandKind.GroupHeader));
        Assert.Equal(new[] { "50", "40" }, Texts(result, BandKind.Detail));
        Assert.Equal(new[] { "90/2" }, Texts(result, BandKind.ReportFooter));
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public void Top_n_without_a_sort_expression_takes_the_first_groups_in_key_order()
    {
        var result = Run(Report("", descending: false, topN: 2), Sample());

        Assert.Equal(new[] { "A", "B" }, Texts(result, BandKind.GroupHeader));
    }

    [Fact]
    public void Equal_totals_keep_the_key_order()
    {
        var rows = new[] { Row("A", 10), Row("B", 10), Row("C", 10) };

        var result = Run(Report("Sum(Amount)", descending: true, topN: 0), rows);

        Assert.Equal(new[] { "A", "B", "C" }, Texts(result, BandKind.GroupHeader));
    }

    [Fact]
    public void The_rows_inside_a_kept_group_stay_in_order()
    {
        var rows = new[] { Row("A", 1), Row("A", 2), Row("A", 3), Row("B", 100) };

        var result = Run(Report("Sum(Amount)", descending: true, topN: 0), rows);

        Assert.Equal(new[] { "100", "1", "2", "3" }, Texts(result, BandKind.Detail));
    }

    [Fact]
    public void An_inner_group_is_ordered_and_trimmed_inside_each_outer_group()
    {
        var def = Report("", descending: false, topN: 0);
        def.Groups.Add(new GroupDefinition { Expression = "Cust", SortExpression = "Sum(Amount)", SortDescending = true, TopN = 1 });
        def.Groups[1].Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Cust}", Width = 100, Height = 18 });
        var rows = new[]
        {
            Row("A", 5, "p"), Row("A", 50, "q"), Row("A", 7, "r"),
            Row("B", 9, "p"), Row("B", 1, "q")
        };

        var result = Run(def, rows);

        Assert.Equal(new[] { "q", "p" }, Texts(result, BandKind.GroupHeader).Where(t => t is "p" or "q" or "r"));
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public void A_bad_sort_expression_is_a_warning_and_the_key_order_stays()
    {
        var result = Run(Report("Nope(1)", descending: true, topN: 0), Sample());

        Assert.Equal(new[] { "A", "B", "C", "D" }, Texts(result, BandKind.GroupHeader));
        Assert.Single(result.Warnings, w => w.StartsWith("Group sort 'Nope(1)'"));
    }

    [Fact]
    public void The_settings_survive_saving_and_loading()
    {
        var group = Report("Sum(Amount)", descending: true, topN: 5).Clone().Groups[0];

        Assert.Equal("Sum(Amount)", group.SortExpression);
        Assert.True(group.SortDescending);
        Assert.Equal(5, group.TopN);
    }
}
