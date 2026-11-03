using ReportStudio.Core.Expressions;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The function list of the expression editor (requirement 23) must match the engine.</summary>
public class ExpressionFunctionsTests
{
    [Fact]
    public void Every_listed_function_is_known_to_the_engine()
    {
        foreach (var f in ExpressionFunctions.All)
        {
            try { Expr.Evaluate(f.Name + "()", new EvalContext()); }
            catch (ExpressionException ex) { Assert.DoesNotContain("Unknown function", ex.Message); }
            catch (Exception) { /* a known function that dislikes having no arguments */ }
        }
    }

    [Fact]
    public void An_unknown_function_is_still_unknown_so_the_check_above_means_something()
    {
        var ex = Assert.Throws<ExpressionException>(() => Expr.Evaluate("NoSuchFunction()", new EvalContext()));
        Assert.Contains("Unknown function", ex.Message);
    }

    [Fact]
    public void Every_example_is_a_valid_expression()
    {
        foreach (var f in ExpressionFunctions.All)
            Assert.True(DefinitionChecks.CheckExpression(f.Example) == null, $"{f.Name}: {f.Example}");
    }

    [Fact]
    public void Names_are_unique_and_every_entry_is_filled_in()
    {
        var names = ExpressionFunctions.All.Select(f => f.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ExpressionFunctions.All, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Signature));
            Assert.False(string.IsNullOrWhiteSpace(f.Description));
            Assert.StartsWith(f.Name, f.Signature);
        });
    }

    [Fact]
    public void The_main_functions_are_in_the_list()
    {
        var names = ExpressionFunctions.All.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var expected in new[] { "Sum", "Count", "IIf", "Format", "AmountInWords", "FinancialYear", "Range" })
            Assert.Contains(expected, names);
    }
}
