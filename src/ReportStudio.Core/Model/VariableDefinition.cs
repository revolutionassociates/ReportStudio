namespace ReportStudio.Core.Model;

/// <summary>How a variable combines the value of each printed detail row into its running value.</summary>
public enum VariableFunction
{
    /// <summary>Adds the value of every row (running total).</summary>
    Sum,
    /// <summary>Counts rows, or the rows where the value is not empty when an expression is given.</summary>
    Count,
    Min,
    Max,
    Average,
    /// <summary>
    /// The expression becomes the new value and may use the variable itself, which gives the value so far,
    /// e.g. Balance = "Balance + Credit - Debit". This is the "shared variable" of other report tools.
    /// </summary>
    Assign
}

/// <summary>When a variable goes back to its starting value.</summary>
public enum VariableReset
{
    /// <summary>Runs through the whole report.</summary>
    Never,
    /// <summary>Starts again whenever the group named by <see cref="VariableDefinition.ResetGroup"/> changes.</summary>
    Group,
    /// <summary>Starts again on every page.</summary>
    Page
}

/// <summary>
/// A named value that is updated as detail rows print, and can be shown in any band as {Name}.
/// Running totals, running counts and balances are all variables. A field of the same name in the data wins.
/// </summary>
public class VariableDefinition
{
    /// <summary>Letters, digits and underscore, not starting with a digit, e.g. "RunningTotal".</summary>
    public string Name { get; set; } = "";

    public VariableFunction Function { get; set; } = VariableFunction.Sum;

    /// <summary>The value taken from each row, e.g. "Amount". Optional for Count.</summary>
    public string Expression { get; set; } = "";

    /// <summary>Only rows where this is true are included. Empty means every row.</summary>
    public string Condition { get; set; } = "";

    /// <summary>The value after a reset (an expression, e.g. "0" or "OpeningBalance"). Empty means 0 for Sum, Count and Assign, and empty for Min, Max and Average.</summary>
    public string Initial { get; set; } = "";

    public VariableReset Reset { get; set; } = VariableReset.Never;

    /// <summary>For Reset = Group: the group level, 1 being the outermost group.</summary>
    public int ResetGroup { get; set; } = 1;
}
