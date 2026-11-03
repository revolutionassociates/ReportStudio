using ReportStudio.Core.Expressions;

namespace ReportStudio.Core.Model;

/// <summary>
/// Checks that the designer runs on what the user types before it is stored in the report, so a mistake is shown
/// in a message instead of failing later when the report runs. Kept here (not in the dialog) so it can be tested.
/// </summary>
public static class DefinitionChecks
{
    /// <summary>The problem with an expression, or null when it is empty or fine. Only the syntax is checked.</summary>
    public static string? CheckExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        try
        {
            Expr.Compile(expression);
            return null;
        }
        catch (ExpressionException ex) { return ex.Message; }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Every problem in a list of variables, one message each; empty when all are fine.
    /// <paramref name="groupCount"/> is the number of groups the report has (a variable can reset on one of them).
    /// </summary>
    public static List<string> CheckVariables(IEnumerable<VariableDefinition> variables, int groupCount)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in variables)
        {
            var label = string.IsNullOrWhiteSpace(v.Name) ? "A variable without a name" : $"Variable '{v.Name}'";

            if (!ReportDefinition.IsValidVariableName(v.Name))
                problems.Add($"{label}: the name must use letters, digits and _ only, and not start with a digit.");
            else if (!seen.Add(v.Name))
                problems.Add($"{label}: the name is used twice.");

            if (v.Function != VariableFunction.Count && string.IsNullOrWhiteSpace(v.Expression))
                problems.Add($"{label}: enter the value to {Describe(v.Function)} (for example Amount).");

            AddExpressionProblem(problems, label, "value", v.Expression);
            AddExpressionProblem(problems, label, "'only when' condition", v.Condition);
            AddExpressionProblem(problems, label, "start value", v.Initial);

            if (v.Reset == VariableReset.Group)
            {
                if (groupCount == 0)
                    problems.Add($"{label}: it resets on a group, but the report has no groups.");
                else if (v.ResetGroup < 1 || v.ResetGroup > groupCount)
                    problems.Add($"{label}: the group level must be between 1 and {groupCount}.");
            }
        }
        return problems;
    }

    /// <summary>
    /// Every problem in an element's conditional formatting rules, one message each; empty when all are fine.
    /// A colour is "#RRGGBB", "#AARRGGBB" or a colour name (names are not verified here).
    /// </summary>
    public static List<string> CheckRules(IEnumerable<FormatRule> rules)
    {
        var problems = new List<string>();
        int number = 0;
        foreach (var rule in rules)
        {
            number++;
            var label = $"Rule {number}";

            if (string.IsNullOrWhiteSpace(rule.Condition))
                problems.Add($"{label}: enter a condition (a rule without one never applies).");
            else
                AddExpressionProblem(problems, label, "condition", rule.Condition);

            if (!IsColorText(rule.ForeColor)) problems.Add($"{label}: the text colour '{rule.ForeColor}' is not a colour (use #RRGGBB).");
            if (!IsColorText(rule.BackColor)) problems.Add($"{label}: the fill colour '{rule.BackColor}' is not a colour (use #RRGGBB).");
            if (!IsColorText(rule.BorderColor)) problems.Add($"{label}: the border colour '{rule.BorderColor}' is not a colour (use #RRGGBB).");

            bool changesNothing = string.IsNullOrWhiteSpace(rule.ForeColor) && string.IsNullOrWhiteSpace(rule.BackColor) &&
                                  string.IsNullOrWhiteSpace(rule.BorderColor) &&
                                  rule.Bold == null && rule.Italic == null && rule.Underline == null;
            if (changesNothing) problems.Add($"{label}: it changes nothing. Choose a colour or a font style.");
        }
        return problems;
    }

    /// <summary>True for empty text, a well-formed hex colour, or a plain colour name.</summary>
    public static bool IsColorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        text = text.Trim();
        if (text[0] != '#') return text.All(char.IsLetter);
        return (text.Length == 7 || text.Length == 9) && text.Skip(1).All(Uri.IsHexDigit);
    }

    private static void AddExpressionProblem(List<string> problems, string label, string what, string? expression)
    {
        var problem = CheckExpression(expression);
        if (problem != null) problems.Add($"{label}: the {what} is not valid ({problem})");
    }

    private static string Describe(VariableFunction function) => function switch
    {
        VariableFunction.Sum => "add up",
        VariableFunction.Min => "find the smallest of",
        VariableFunction.Max => "find the largest of",
        VariableFunction.Average => "average",
        VariableFunction.Assign => "assign",
        _ => "use"
    };
}
