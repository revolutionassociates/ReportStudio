namespace ReportStudio.Designer;

/// <summary>
/// What the expression editor can offer besides the built-in functions: the report's fields, parameters and variables.
/// The main window refreshes it whenever the data source or the settings change.
/// </summary>
internal static class ExpressionEditorContext
{
    /// <summary>Field names (written in [brackets] when they contain spaces) and parameters as @Name.</summary>
    public static IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();

    public static IReadOnlyList<string> Variables { get; set; } = Array.Empty<string>();
}
