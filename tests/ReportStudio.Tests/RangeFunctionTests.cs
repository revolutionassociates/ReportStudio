using System.Globalization;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Grouping by ranges: months, quarters, financial years and number buckets (requirement 20).</summary>
public class RangeFunctionTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static object? Eval(string expression, params (string K, object? V)[] row)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in row) d[k] = v;
        return Expr.Evaluate(expression, new EvalContext { Row = d, Culture = Inv });
    }

    [Fact]
    public void Dates_fall_back_to_the_start_of_their_month_quarter_year_and_week()
    {
        var date = new DateTime(2026, 5, 15);     // a Friday

        Assert.Equal(new DateTime(2026, 5, 1), Eval("MonthStart(D)", ("D", date)));
        Assert.Equal(new DateTime(2026, 4, 1), Eval("QuarterStart(D)", ("D", date)));
        Assert.Equal(new DateTime(2026, 1, 1), Eval("YearStart(D)", ("D", date)));
        Assert.Equal(new DateTime(2026, 5, 11), Eval("WeekStart(D)", ("D", date)));      // the Monday before
        Assert.Equal(new DateTime(2026, 5, 11), Eval("WeekStart(D)", ("D", new DateTime(2026, 5, 11))));
        Assert.Equal(new DateTime(2026, 5, 4), Eval("WeekStart(D)", ("D", new DateTime(2026, 5, 10))));   // a Sunday
    }

    [Theory]
    [InlineData(2026, 3, 31, "2025-26")]
    [InlineData(2026, 4, 1, "2026-27")]
    [InlineData(2026, 12, 31, "2026-27")]
    [InlineData(2099, 4, 1, "2099-00")]
    public void The_financial_year_runs_april_to_march(int year, int month, int day, string expected)
        => Assert.Equal(expected, Eval("FinancialYear(D)", ("D", new DateTime(year, month, day))));

    [Fact]
    public void Missing_or_wrong_values_give_null()
    {
        Assert.Null(Eval("MonthStart(D)", ("D", null)));
        Assert.Null(Eval("FinancialYear(D)", ("D", "not a date")));
        Assert.Null(Eval("Bucket(A, 1000)", ("A", null)));
        Assert.Null(Eval("Bucket(A, 0)", ("A", 5m)));
        Assert.Null(Eval("Range(A, 1000)", ("A", null)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(999.99, 0)]
    [InlineData(1000, 1000)]
    [InlineData(1750, 1000)]
    [InlineData(-1, -1000)]
    public void Bucket_gives_the_lower_end_of_an_equal_sized_range(double amount, double expected)
        => Assert.Equal((decimal)expected, Eval("Bucket(A, 1000)", ("A", (decimal)amount)));

    [Fact]
    public void Range_gives_the_lower_limit_of_the_range_with_your_own_limits()
    {
        Assert.Null(Eval("Range(A, 1000, 5000, 20000)", ("A", 999m)));              // below the first limit
        Assert.Equal(1000m, Eval("Range(A, 1000, 5000, 20000)", ("A", 1000m)));
        Assert.Equal(1000m, Eval("Range(A, 1000, 5000, 20000)", ("A", 4999.99m)));
        Assert.Equal(5000m, Eval("Range(A, 1000, 5000, 20000)", ("A", 5000m)));
        Assert.Equal(20000m, Eval("Range(A, 1000, 5000, 20000)", ("A", 1e9m)));
        Assert.Equal(1000m, Eval("Range(A, 20000, 1000, 5000)", ("A", 2000m)));      // limits can be in any order
    }

    [Fact]
    public void RangeLabel_reads_well_in_a_group_header()
    {
        Assert.Equal("Below 1,000", Eval("RangeLabel(A, 1000, 5000)", ("A", 5m)));
        Assert.Equal("1,000 to below 5,000", Eval("RangeLabel(A, 1000, 5000)", ("A", 1000m)));
        Assert.Equal("5,000 and above", Eval("RangeLabel(A, 1000, 5000)", ("A", 5000m)));
    }

    [Fact]
    public void A_report_can_group_by_month_and_show_a_readable_heading()
    {
        var def = new ReportDefinition { Name = "ByMonth" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.PageFooter.Height = 0;
        def.ReportFooter.Height = 0;
        def.Detail.Height = 20;
        def.Groups.Add(new GroupDefinition { Expression = "MonthStart(Date)" });
        def.Groups[0].Header.Items.Add(new ReportItem
        {
            Kind = ItemKind.Text, Text = "{Format(MonthStart(Date), 'MMM yyyy')}", Width = 100, Height = 18
        });
        def.Groups[0].Footer.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Sum(Amount)}", Width = 100, Height = 18 });

        Dictionary<string, object?> Row(DateTime date, decimal amount) =>
            new(StringComparer.OrdinalIgnoreCase) { ["Date"] = date, ["Amount"] = amount };

        var rows = new[]
        {
            Row(new DateTime(2026, 2, 20), 5), Row(new DateTime(2026, 1, 10), 1),
            Row(new DateTime(2026, 2, 1), 7), Row(new DateTime(2026, 1, 31), 2)
        };

        var result = ReportEngine.Run(def, null, rows, new ReportOptions { Culture = Inv });
        var items = result.Pages.SelectMany(p => p.Items).ToList();

        // Months come out in date order (not by the order of the rows), each with its own total.
        Assert.Equal(new[] { "Jan 2026", "Feb 2026" }, items.Where(i => i.Band == BandKind.GroupHeader).Select(i => i.Text));
        Assert.Equal(new[] { "3", "12" }, items.Where(i => i.Band == BandKind.GroupFooter).Select(i => i.Text));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_report_can_group_by_number_ranges_in_range_order()
    {
        var def = new ReportDefinition { Name = "ByRange" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.PageFooter.Height = 0;
        def.ReportFooter.Height = 0;
        def.Detail.Height = 20;
        def.Groups.Add(new GroupDefinition { Expression = "Range(Amount, 1000, 5000)" });
        def.Groups[0].Header.Items.Add(new ReportItem
        {
            Kind = ItemKind.Text, Text = "{RangeLabel(Amount, 1000, 5000)}", Width = 100, Height = 18
        });

        Dictionary<string, object?> Row(decimal amount) => new(StringComparer.OrdinalIgnoreCase) { ["Amount"] = amount };

        var result = ReportEngine.Run(def, null, new[] { Row(9000), Row(100), Row(2000), Row(60000), Row(300) },
            new ReportOptions { Culture = Inv });

        Assert.Equal(new[] { "Below 1,000", "1,000 to below 5,000", "5,000 and above" },
            result.Pages.SelectMany(p => p.Items).Where(i => i.Band == BandKind.GroupHeader).Select(i => i.Text));
    }
}
