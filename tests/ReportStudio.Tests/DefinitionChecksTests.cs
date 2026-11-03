using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The checks behind the designer's variables and filter settings (requirement 23).</summary>
public class DefinitionChecksTests
{
    private static VariableDefinition Var(string name, string expression = "Amount", Action<VariableDefinition>? configure = null)
    {
        var v = new VariableDefinition { Name = name, Expression = expression };
        configure?.Invoke(v);
        return v;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Amount > 100")]
    [InlineData("Status == 'Active' && Net > 0")]
    public void A_fine_or_empty_expression_has_no_problem(string expression) =>
        Assert.Null(DefinitionChecks.CheckExpression(expression));

    [Theory]
    [InlineData("(1 +")]
    [InlineData("Sum(")]
    [InlineData("1 +")]
    public void A_broken_expression_gives_a_message(string expression) =>
        Assert.False(string.IsNullOrWhiteSpace(DefinitionChecks.CheckExpression(expression)));

    [Fact]
    public void A_null_expression_is_fine() => Assert.Null(DefinitionChecks.CheckExpression(null));

    [Fact]
    public void Good_variables_have_no_problems()
    {
        var problems = DefinitionChecks.CheckVariables(new[]
        {
            Var("RunningTotal"),
            Var("RowCount", "", v => v.Function = VariableFunction.Count),
            Var("GroupTotal", "Amount", v => { v.Reset = VariableReset.Group; v.ResetGroup = 1; })
        }, groupCount: 1);

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1st")]
    [InlineData("Run total")]
    public void A_bad_name_is_reported(string name)
    {
        var problems = DefinitionChecks.CheckVariables(new[] { Var(name) }, 0);

        Assert.Contains(problems, p => p.Contains("name"));
    }

    [Fact]
    public void The_same_name_twice_is_reported_whatever_the_case()
    {
        var problems = DefinitionChecks.CheckVariables(new[] { Var("Total"), Var("TOTAL") }, 0);

        Assert.Single(problems);
        Assert.Contains("twice", problems[0]);
    }

    [Fact]
    public void A_sum_needs_a_value_but_a_count_does_not()
    {
        var sum = DefinitionChecks.CheckVariables(new[] { Var("A", "") }, 0);
        var count = DefinitionChecks.CheckVariables(new[] { Var("B", "", v => v.Function = VariableFunction.Count) }, 0);

        Assert.Single(sum);
        Assert.Empty(count);
    }

    [Fact]
    public void A_broken_value_condition_or_start_value_is_reported()
    {
        var problems = DefinitionChecks.CheckVariables(new[]
        {
            Var("A", "(1 +"),
            Var("B", "Amount", v => v.Condition = "Sum("),
            Var("C", "Amount", v => v.Initial = "1 +")
        }, 0);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("'A'") && p.Contains("value"));
        Assert.Contains(problems, p => p.Contains("'B'") && p.Contains("condition"));
        Assert.Contains(problems, p => p.Contains("'C'") && p.Contains("start value"));
    }

    [Fact]
    public void Resetting_on_a_group_needs_that_group_to_exist()
    {
        Action<VariableDefinition> onGroup2 = v => { v.Reset = VariableReset.Group; v.ResetGroup = 2; };

        Assert.Single(DefinitionChecks.CheckVariables(new[] { Var("A", "Amount", onGroup2) }, groupCount: 0));
        Assert.Single(DefinitionChecks.CheckVariables(new[] { Var("A", "Amount", onGroup2) }, groupCount: 1));
        Assert.Empty(DefinitionChecks.CheckVariables(new[] { Var("A", "Amount", onGroup2) }, groupCount: 2));
    }

    // ---- conditional formatting rules (requirement 24) ----

    private static FormatRule Rule(string condition, string fore = "#B91C1C", Action<FormatRule>? configure = null)
    {
        var r = new FormatRule { Condition = condition, ForeColor = fore };
        configure?.Invoke(r);
        return r;
    }

    [Fact]
    public void Good_rules_have_no_problems()
    {
        var problems = DefinitionChecks.CheckRules(new[]
        {
            Rule("Net < 0"),
            Rule("Status == 'Overdue'", "", r => { r.BackColor = "#FEE2E2"; r.Bold = true; }),
            Rule("Net > 0", "Green")
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void A_rule_needs_a_valid_condition()
    {
        var problems = DefinitionChecks.CheckRules(new[] { Rule(""), Rule("(1 +") });

        Assert.Equal(2, problems.Count);
        Assert.Contains("Rule 1", problems[0]);
        Assert.Contains("Rule 2", problems[1]);
    }

    [Fact]
    public void A_rule_that_changes_nothing_is_reported()
    {
        var problems = DefinitionChecks.CheckRules(new[] { Rule("Net < 0", "") });

        Assert.Single(problems);
        Assert.Contains("changes nothing", problems[0]);
    }

    [Theory]
    [InlineData("#B91C1C", true)]
    [InlineData("#80B91C1C", true)]
    [InlineData("Red", true)]
    [InlineData("", true)]
    [InlineData("#B91C1", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("12", false)]
    public void Colours_are_checked_for_form(string text, bool expected) =>
        Assert.Equal(expected, DefinitionChecks.IsColorText(text));

    [Fact]
    public void A_badly_written_colour_is_reported_with_its_rule_number()
    {
        var problems = DefinitionChecks.CheckRules(new[] { Rule("Net < 0"), Rule("Net > 0", "#12") });

        Assert.Single(problems);
        Assert.Contains("Rule 2", problems[0]);
        Assert.Contains("text colour", problems[0]);
    }

    [Fact]
    public void The_group_level_is_ignored_unless_the_variable_resets_on_a_group()
    {
        var problems = DefinitionChecks.CheckVariables(
            new[] { Var("A", "Amount", v => { v.Reset = VariableReset.Page; v.ResetGroup = 9; }) }, groupCount: 0);

        Assert.Empty(problems);
    }
}
