using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Running totals and shared variables (requirement 16).</summary>
public class VariableTests
{
    private static Dictionary<string, object?> Row(string dept, decimal amount) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Dept"] = dept, ["Amount"] = amount };

    private static ReportDefinition Report(params VariableDefinition[] variables)
    {
        var def = new ReportDefinition { Name = "Vars" };
        def.DataSource.Provider = DbProvider.Host;
        def.Variables.AddRange(variables);
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 20;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        return def;
    }

    private static void Add(Band band, string text) =>
        band.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = text, X = 0, Y = 0, Width = 200, Height = 18 });

    private static ReportResult Run(ReportDefinition def, params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static List<string> Texts(ReportResult result, BandKind band) =>
        result.Pages.SelectMany(p => p.Items).Where(i => i.Band == band).Select(i => i.Text).ToList();

    private static VariableDefinition Total(string name = "RT") =>
        new() { Name = name, Function = VariableFunction.Sum, Expression = "Amount" };

    [Fact]
    public void A_running_total_includes_its_own_line()
    {
        var def = Report(Total());
        Add(def.Detail, "{RT}");
        Add(def.ReportFooter, "{RT}");

        var result = Run(def, Row("A", 10), Row("A", 20), Row("B", 30));

        Assert.Equal(new[] { "10", "30", "60" }, Texts(result, BandKind.Detail));
        Assert.Equal(new[] { "60" }, Texts(result, BandKind.ReportFooter));
    }

    [Fact]
    public void A_group_reset_restarts_the_total_for_each_group()
    {
        var total = Total();
        total.Reset = VariableReset.Group;
        total.ResetGroup = 1;
        var def = Report(total);
        def.Groups.Add(new GroupDefinition { Expression = "Dept" });
        Add(def.Groups[0].Header, "{RT}");
        Add(def.Groups[0].Footer, "{RT}");

        var result = Run(def, Row("A", 10), Row("A", 20), Row("B", 5), Row("B", 7));

        Assert.Equal(new[] { "0", "0" }, Texts(result, BandKind.GroupHeader));
        Assert.Equal(new[] { "30", "12" }, Texts(result, BandKind.GroupFooter));
    }

    [Fact]
    public void A_page_reset_restarts_on_each_page_and_the_footer_excludes_the_next_page()
    {
        var pageTotal = Total("PT");
        pageTotal.Reset = VariableReset.Page;
        var def = Report(pageTotal);
        def.Page.Height = 120;
        def.Page.MarginTop = 10;
        def.Page.MarginBottom = 10;
        def.PageFooter.Height = 30;          // body runs from 10 to 80: three 20-high rows fit, the fourth moves on
        def.ReportFooter.Height = 0;
        Add(def.Detail, "{PT}");
        Add(def.PageFooter, "{PT}");

        var result = Run(def, Row("A", 1), Row("A", 2), Row("A", 3), Row("A", 4), Row("A", 5));

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(new[] { "1", "3", "6" }, result.Pages[0].Items.Where(i => i.Band == BandKind.Detail).Select(i => i.Text));
        Assert.Equal(new[] { "4", "9" }, result.Pages[1].Items.Where(i => i.Band == BandKind.Detail).Select(i => i.Text));
        Assert.Equal("6", result.Pages[0].Items.Single(i => i.Band == BandKind.PageFooter).Text);
        Assert.Equal("9", result.Pages[1].Items.Single(i => i.Band == BandKind.PageFooter).Text);
    }

    [Fact]
    public void An_assign_variable_keeps_a_balance_from_its_own_previous_value()
    {
        var balance = new VariableDefinition
        {
            Name = "Balance", Function = VariableFunction.Assign, Expression = "Balance + Amount", Initial = "1000"
        };
        var def = Report(balance);
        Add(def.Detail, "{Balance}");

        var result = Run(def, Row("A", 500), Row("A", -200));

        Assert.Equal(new[] { "1500", "1300" }, Texts(result, BandKind.Detail));
    }

    [Fact]
    public void A_condition_limits_which_rows_count()
    {
        var big = new VariableDefinition { Name = "Big", Function = VariableFunction.Count, Condition = "Amount > 15" };
        var def = Report(big);
        Add(def.ReportFooter, "{Big}");

        var result = Run(def, Row("A", 10), Row("A", 20), Row("A", 30));

        Assert.Equal(new[] { "2" }, Texts(result, BandKind.ReportFooter));
    }

    [Fact]
    public void Min_max_and_average_follow_the_rows()
    {
        var def = Report(
            new VariableDefinition { Name = "Lo", Function = VariableFunction.Min, Expression = "Amount" },
            new VariableDefinition { Name = "Hi", Function = VariableFunction.Max, Expression = "Amount" },
            new VariableDefinition { Name = "Mean", Function = VariableFunction.Average, Expression = "Amount" });
        Add(def.ReportFooter, "{Lo}/{Hi}/{Mean}");

        var result = Run(def, Row("A", 10), Row("A", 20), Row("A", 30));

        Assert.Equal(new[] { "10/30/20" }, Texts(result, BandKind.ReportFooter));
    }

    [Fact]
    public void A_data_field_of_the_same_name_wins_over_the_variable()
    {
        var def = Report(new VariableDefinition { Name = "Amount", Function = VariableFunction.Sum, Expression = "Amount" });
        Add(def.Detail, "{Amount}");

        var result = Run(def, Row("A", 10), Row("A", 20));

        Assert.Equal(new[] { "10", "20" }, Texts(result, BandKind.Detail));
    }

    [Fact]
    public void A_bad_expression_is_a_warning_not_a_crash()
    {
        var def = Report(new VariableDefinition { Name = "Oops", Function = VariableFunction.Sum, Expression = "Nope(1)" });
        Add(def.Detail, "{Oops}");

        var result = Run(def, Row("A", 10));

        Assert.Contains(result.Warnings, w => w.Contains("Oops"));
    }

    [Fact]
    public void Unusable_and_duplicate_names_are_dropped_and_the_rest_survive_json()
    {
        var def = Report(
            Total("Good"),
            Total("good"),            // same name, different case
            Total("1bad"),
            Total("has space"),
            Total(""));

        var copy = def.Clone();

        Assert.Equal(new[] { "Good" }, copy.Variables.Select(v => v.Name));
        Assert.Equal(VariableFunction.Sum, copy.Variables[0].Function);
        Assert.Equal("Amount", copy.Variables[0].Expression);
    }
}
