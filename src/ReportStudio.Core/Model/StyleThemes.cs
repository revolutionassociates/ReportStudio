namespace ReportStudio.Core.Model;

/// <summary>A ready-made set of styles with the standard names of <see cref="StyleThemes"/>.</summary>
public sealed record StyleTheme(string Name, string Description, Func<List<StyleDefinition>> Create)
{
    public override string ToString() => Name;
}

/// <summary>
/// The themes the designer offers (requirement 46): each is a set of styles with the same names, so a report whose elements use those
/// names can be given another look by applying another theme, without touching the elements.
/// </summary>
public static class StyleThemes
{
    // The standard style names. A theme defines every one of them.
    public const string Title = "Title";
    public const string Subtitle = "Subtitle";
    public const string Heading = "Heading";
    public const string ColumnTitle = "Column title";
    public const string Body = "Body";
    public const string Total = "Total";
    public const string Note = "Note";

    /// <summary>The names every theme defines, in this order.</summary>
    public static IReadOnlyList<string> StandardNames { get; } = new[] { Title, Subtitle, Heading, ColumnTitle, Body, Total, Note };

    private sealed record Palette(string Title, string HeaderBack, string HeaderText, string GroupBack, string Muted, string Text);

    public static IReadOnlyList<StyleTheme> All { get; } = new[]
    {
        new StyleTheme("Corporate", "Navy headings and column titles on a pale blue band.",
            () => Build(new Palette("#1F3A5F", "#1F3A5F", "#FFFFFF", "#E6EEF7", "#555555", "#111111"))),
        new StyleTheme("Simple", "Dark grey text with light grey bands.",
            () => Build(new Palette("#111827", "#E5E7EB", "#111827", "#F3F4F6", "#6B7280", "#111827"))),
        new StyleTheme("Modern", "Teal headings and column titles on a pale teal band.",
            () => Build(new Palette("#0F766E", "#0F766E", "#FFFFFF", "#E0F2F1", "#4B5563", "#1F2937"))),
        new StyleTheme("Classic", "Black text, no fills.",
            () => Build(new Palette("#000000", "", "#000000", "", "#444444", "#000000")))
    };

    public static StyleTheme? Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts the styles of the theme into the report: a style of the same name is replaced, the others stay. Elements are not touched.
    /// Returns how many styles were added or replaced.
    /// </summary>
    public static int Apply(ReportDefinition def, StyleTheme theme)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(theme);

        int changed = 0;
        foreach (var style in theme.Create())
        {
            int at = def.Styles.FindIndex(s => string.Equals(s.Name?.Trim(), style.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) def.Styles[at] = style;
            else def.Styles.Add(style);
            changed++;
        }
        return changed;
    }

    private static List<StyleDefinition> Build(Palette p) => new()
    {
        new() { Name = Title, FontSize = 18, Bold = true, ForeColor = p.Title },
        new() { Name = Subtitle, FontSize = 11, Bold = false, ForeColor = p.Muted },
        new() { Name = Heading, FontSize = 12, Bold = true, ForeColor = p.Title, BackColor = p.GroupBack },
        new() { Name = ColumnTitle, FontSize = 9, Bold = true, ForeColor = p.HeaderText, BackColor = p.HeaderBack },
        new() { Name = Body, FontSize = 9, Bold = false, ForeColor = p.Text },
        new() { Name = Total, FontSize = 10, Bold = true, ForeColor = p.Text },
        new() { Name = Note, FontSize = 8, Italic = true, ForeColor = p.Muted }
    };
}
