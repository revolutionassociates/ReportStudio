using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Conditional formatting (requirement 17).</summary>
public class ConditionalFormatTests
{
    private static Dictionary<string, object?> Row(decimal amount, string status = "Paid") =>
        new(StringComparer.OrdinalIgnoreCase) { ["Amount"] = amount, ["Status"] = status };

    private static ReportDefinition Report(ReportItem item, params VariableDefinition[] variables)
    {
        var def = new ReportDefinition { Name = "Rules" };
        def.DataSource.Provider = DbProvider.Host;
        def.Variables.AddRange(variables);
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        def.Detail.Items.Add(item);
        return def;
    }

    private static ReportItem Cell(params FormatRule[] rules)
    {
        var item = new ReportItem { Kind = ItemKind.Text, Text = "{Amount}", Width = 100, Height = 18, ForeColor = "#000000" };
        item.Rules.AddRange(rules);
        return item;
    }

    private static List<RenderedItem> Run(ReportDefinition def, params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture })
            .Pages.SelectMany(p => p.Items).Where(i => i.Band == BandKind.Detail).ToList();

    [Fact]
    public void A_true_rule_changes_only_the_rows_that_match()
    {
        var def = Report(Cell(new FormatRule { Condition = "Amount < 0", ForeColor = "#FF0000", Bold = true }));

        var items = Run(def, Row(10), Row(-5), Row(7));

        Assert.Equal(new[] { "#000000", "#FF0000", "#000000" }, items.Select(i => i.ForeColor));
        Assert.Equal(new[] { false, true, false }, items.Select(i => i.Bold));
    }

    [Fact]
    public void Rules_apply_in_order_so_a_later_rule_wins()
    {
        var def = Report(Cell(
            new FormatRule { Condition = "Amount > 0", ForeColor = "#00AA00", BackColor = "#EEFFEE" },
            new FormatRule { Condition = "Amount > 100", ForeColor = "#0000FF" }));

        var items = Run(def, Row(50), Row(500));

        Assert.Equal("#00AA00", items[0].ForeColor);
        Assert.Equal("#0000FF", items[1].ForeColor);
        Assert.Equal("#EEFFEE", items[1].BackColor);     // set by the first rule, not undone by the second
    }

    [Fact]
    public void Settings_a_rule_leaves_empty_are_not_touched()
    {
        var item = Cell(new FormatRule { Condition = "true", BackColor = "#FFFF00" });
        item.Bold = true;
        item.ForeColor = "#112233";

        var shown = Run(Report(item), Row(1)).Single();

        Assert.Equal("#112233", shown.ForeColor);
        Assert.True(shown.Bold);
        Assert.Equal("#FFFF00", shown.BackColor);
    }

    [Fact]
    public void A_rule_can_switch_a_style_off()
    {
        var item = Cell(new FormatRule { Condition = "Status == 'Paid'", Bold = false });
        item.Bold = true;

        Assert.False(Run(Report(item), Row(1, "Paid")).Single().Bold);
        Assert.True(Run(Report(item), Row(1, "Due")).Single().Bold);
    }

    [Fact]
    public void A_rule_can_use_a_running_total()
    {
        var total = new VariableDefinition { Name = "RT", Function = VariableFunction.Sum, Expression = "Amount" };
        var def = Report(Cell(new FormatRule { Condition = "RT > 100", BackColor = "#FFCCCC" }), total);

        var items = Run(def, Row(60), Row(60));

        Assert.Equal(new[] { "", "#FFCCCC" }, items.Select(i => i.BackColor));
    }

    [Fact]
    public void A_bad_condition_is_a_warning_and_the_item_keeps_its_design()
    {
        var def = Report(Cell(new FormatRule { Condition = "Nope(1)", ForeColor = "#FF0000" }));

        var result = ReportEngine.Run(def, null, new[] { Row(1) }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
        var item = result.Pages.SelectMany(p => p.Items).Single(i => i.Band == BandKind.Detail);

        Assert.Equal("#000000", item.ForeColor);
        Assert.Contains(result.Warnings, w => w.Contains("Format rule"));
    }

    [Fact]
    public void The_design_is_never_changed_by_a_run()
    {
        var item = Cell(new FormatRule { Condition = "true", ForeColor = "#FF0000" });
        Run(Report(item), Row(1));
        Assert.Equal("#000000", item.ForeColor);
    }

    [Fact]
    public void A_copied_item_gets_its_own_rules()
    {
        var original = Cell(new FormatRule { Condition = "Amount < 0", ForeColor = "#FF0000" });
        var copy = original.Clone();

        copy.Rules[0].ForeColor = "#0000FF";
        copy.Rules.Add(new FormatRule { Condition = "true" });

        Assert.Equal("#FF0000", original.Rules[0].ForeColor);
        Assert.Single(original.Rules);
    }

    [Fact]
    public void Rules_survive_saving_and_loading()
    {
        var def = Report(Cell(new FormatRule { Condition = "Amount < 0", ForeColor = "#FF0000", Bold = true, Italic = false }));

        var rule = def.Clone().Detail.Items[0].Rules.Single();

        Assert.Equal("Amount < 0", rule.Condition);
        Assert.Equal("#FF0000", rule.ForeColor);
        Assert.True(rule.Bold);
        Assert.False(rule.Italic);
        Assert.Null(rule.Underline);
    }
}
