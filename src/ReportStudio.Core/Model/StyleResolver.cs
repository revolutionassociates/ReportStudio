namespace ReportStudio.Core.Model;

/// <summary>
/// Finds styles and puts their values on the elements that use them (requirement 46). The engine resolves a report once before it lays it out,
/// so every preview, print and export sees the styled elements; conditional formats still apply on top of the style.
/// </summary>
public static class StyleResolver
{
    /// <summary>How many "based on" steps are followed; a longer chain is cut off here.</summary>
    public const int MaxDepth = 8;

    /// <summary>The style of that name (without regard to case), or null.</summary>
    public static StyleDefinition? Find(ReportDefinition def, string? name)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;
        return def.Styles.FirstOrDefault(s => string.Equals(s.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The style with the values it gets from the styles it is based on filled in (the nearest style wins). A missing parent or a style
    /// that is based on itself ends the chain there. Null when there is no style of that name.
    /// </summary>
    public static StyleDefinition? Effective(ReportDefinition def, string? name)
    {
        var leaf = Find(def, name);
        if (leaf == null) return null;

        var chain = new List<StyleDefinition> { leaf };
        var seen = new HashSet<StyleDefinition> { leaf };
        var current = leaf;
        while (chain.Count < MaxDepth)
        {
            var parent = Find(def, current.Parent);
            if (parent == null || !seen.Add(parent)) break;
            chain.Add(parent);
            current = parent;
        }

        var result = new StyleDefinition { Name = leaf.Name };
        for (int i = chain.Count - 1; i >= 0; i--) Overlay(result, chain[i]);
        return result;
    }

    /// <summary>Puts every value the style sets on the element. Values it leaves empty are not touched.</summary>
    public static void Apply(StyleDefinition style, ReportItem item)
    {
        if (!string.IsNullOrWhiteSpace(style.FontFamily)) item.FontFamily = style.FontFamily.Trim();
        if (style.FontSize is double size && double.IsFinite(size)) item.FontSize = Math.Clamp(size, 6, 600);
        if (style.Bold is bool bold) item.Bold = bold;
        if (style.Italic is bool italic) item.Italic = italic;
        if (style.Underline is bool underline) item.Underline = underline;
        if (style.HAlign is HAlign horizontal) item.HAlign = horizontal;
        if (style.VAlign is VAlign vertical) item.VAlign = vertical;
        if (style.ForeColor != null) item.ForeColor = style.ForeColor.Trim();
        if (style.BackColor != null) item.BackColor = style.BackColor.Trim();
        if (style.BorderColor != null) item.BorderColor = style.BorderColor.Trim();
        if (style.BorderWidth is double width && double.IsFinite(width)) item.BorderWidth = Math.Max(0, width);
        if (style.CornerRadius is double radius && double.IsFinite(radius)) item.CornerRadius = Math.Max(0, radius);
    }

    /// <summary>
    /// The element as it looks with its style: the element itself when it has no style or names one the report does not have, otherwise a copy
    /// with the style's values (the designer draws this; the element is not changed).
    /// </summary>
    public static ReportItem LookOf(ReportDefinition def, ReportItem item)
    {
        var effective = Effective(def, item.Style);
        if (effective == null) return item;
        var copy = item.Clone();
        Apply(effective, copy);
        return copy;
    }

    /// <summary>A style that holds the whole look of the element (font, alignment, colours, border), named <paramref name="name"/>.</summary>
    public static StyleDefinition FromItem(ReportItem item, string name) => new()
    {
        Name = name.Trim(),
        FontFamily = item.FontFamily,
        FontSize = item.FontSize,
        Bold = item.Bold,
        Italic = item.Italic,
        Underline = item.Underline,
        HAlign = item.HAlign,
        VAlign = item.VAlign,
        ForeColor = item.ForeColor ?? "",
        BackColor = item.BackColor ?? "",
        BorderColor = item.BorderColor ?? "",
        BorderWidth = item.BorderWidth,
        CornerRadius = item.CornerRadius
    };

    /// <summary>
    /// The report to lay out: the report itself when no element uses a style, otherwise a copy in which every element that names an existing
    /// style has that style's values. The report that was passed in is never changed.
    /// </summary>
    public static ReportDefinition Resolve(ReportDefinition def)
    {
        if (def.Styles.Count == 0 || !def.AllBands().Any(b => b.Items.Any(i => !string.IsNullOrWhiteSpace(i.Style)))) return def;

        var copy = def.Clone();
        var cache = new Dictionary<string, StyleDefinition?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in copy.AllBands().SelectMany(b => b.Items))
        {
            var name = item.Style?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!cache.TryGetValue(name, out var effective)) cache[name] = effective = Effective(copy, name);
            if (effective != null) Apply(effective, item);
        }
        return copy;
    }

    /// <summary>One warning for every style name that elements use but the report does not have (they keep their own look).</summary>
    public static List<string> Warnings(ReportDefinition def)
    {
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in def.AllBands().SelectMany(b => b.Items))
        {
            var name = item.Style?.Trim();
            if (string.IsNullOrEmpty(name) || Find(def, name) != null || !seen.Add(name)) continue;
            warnings.Add($"Style '{name}' does not exist, so the elements that use it keep their own look.");
        }
        return warnings;
    }

    /// <summary>Everything wrong with the styles of a report, as plain sentences for the person editing them (empty = fine).</summary>
    public static List<string> Check(ReportDefinition def)
    {
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var style in def.Styles)
        {
            var name = style.Name?.Trim() ?? "";
            if (name.Length == 0) { problems.Add("A style has no name."); continue; }
            if (!names.Add(name)) problems.Add($"Two styles are called '{name}'.");

            var parent = style.Parent?.Trim() ?? "";
            if (parent.Length > 0)
            {
                if (Find(def, parent) == null) problems.Add($"Style '{name}' is based on '{parent}', which does not exist.");
                else if (BasesOnItself(def, style)) problems.Add($"Style '{name}' is based on itself (through '{parent}').");
            }

            foreach (var (label, value) in new[] { ("text colour", style.ForeColor), ("fill colour", style.BackColor), ("border colour", style.BorderColor) })
                if (!DefinitionChecks.IsColorText(value)) problems.Add($"Style '{name}': the {label} '{value}' is not a colour (use #RRGGBB).");

            if (style.FontSize is double size && (!double.IsFinite(size) || size < 6 || size > 600))
                problems.Add($"Style '{name}': the font size must be between 6 and 600.");
            if (style.BorderWidth is double width && (!double.IsFinite(width) || width < 0))
                problems.Add($"Style '{name}': the border width cannot be negative.");
            if (style.CornerRadius is double radius && (!double.IsFinite(radius) || radius < 0))
                problems.Add($"Style '{name}': the corner radius cannot be negative.");
        }

        problems.AddRange(Warnings(def).Select(w => w.Replace("so the elements that use it keep their own look.", "but elements use it.")));
        return problems;
    }

    private static bool BasesOnItself(ReportDefinition def, StyleDefinition style)
    {
        var current = style;
        for (int step = 0; step <= def.Styles.Count; step++)
        {
            var parent = Find(def, current.Parent);
            if (parent == null) return false;
            if (ReferenceEquals(parent, style)) return true;
            current = parent;
        }
        return true;   // a loop that does not lead back to this style
    }

    private static void Overlay(StyleDefinition target, StyleDefinition over)
    {
        target.FontFamily = over.FontFamily ?? target.FontFamily;
        target.FontSize = over.FontSize ?? target.FontSize;
        target.Bold = over.Bold ?? target.Bold;
        target.Italic = over.Italic ?? target.Italic;
        target.Underline = over.Underline ?? target.Underline;
        target.HAlign = over.HAlign ?? target.HAlign;
        target.VAlign = over.VAlign ?? target.VAlign;
        target.ForeColor = over.ForeColor ?? target.ForeColor;
        target.BackColor = over.BackColor ?? target.BackColor;
        target.BorderColor = over.BorderColor ?? target.BorderColor;
        target.BorderWidth = over.BorderWidth ?? target.BorderWidth;
        target.CornerRadius = over.CornerRadius ?? target.CornerRadius;
    }
}
