using System.Globalization;

namespace ReportStudio.Core.Model;

/// <summary>
/// A style as one row of text cells, the way the designer's Styles grid shows it (requirement 46b). An empty cell means "not set";
/// Bold, Italic and Underline are Yes or No; a colour cell is #RRGGBB, or <see cref="NoColour"/> for "no colour" (an empty colour in the style).
/// Kept here, away from the window code, so the rules can be tested.
/// </summary>
public static class StyleCells
{
    public const string NoColour = "none";
    public const string Yes = "Yes";
    public const string No = "No";

    /// <summary>The column headings, in cell order.</summary>
    public static IReadOnlyList<string> Columns { get; } = new[]
    {
        "Name", "Based on", "Font", "Size", "Bold", "Italic", "Underline", "Align", "Vertical", "Text colour", "Fill colour", "Border colour",
        "Border width", "Corner radius"
    };

    public const int NameColumn = 0, ParentColumn = 1, FontColumn = 2, SizeColumn = 3, BoldColumn = 4, ItalicColumn = 5, UnderlineColumn = 6,
        AlignColumn = 7, VerticalColumn = 8, TextColourColumn = 9, FillColourColumn = 10, BorderColourColumn = 11,
        BorderWidthColumn = 12, CornerColumn = 13;

    /// <summary>The choices of the Yes / No cells (empty = not set).</summary>
    public static IReadOnlyList<string> YesNoChoices { get; } = new[] { "", Yes, No };
    public static IReadOnlyList<string> AlignChoices { get; } = new[] { "", nameof(HAlign.Left), nameof(HAlign.Center), nameof(HAlign.Right) };
    public static IReadOnlyList<string> VerticalChoices { get; } = new[] { "", nameof(VAlign.Top), nameof(VAlign.Middle), nameof(VAlign.Bottom) };

    /// <summary>The cells of a style, as text (numbers written with <paramref name="culture"/>, default the current one).</summary>
    public static string[] ToCells(StyleDefinition style, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string Number(double? v) => v.HasValue ? v.Value.ToString("0.##", culture) : "";
        string YesNo(bool? v) => v.HasValue ? (v.Value ? Yes : No) : "";
        string Colour(string? v) => v == null ? "" : v.Trim().Length == 0 ? NoColour : v.Trim();

        return new[]
        {
            style.Name ?? "", style.Parent ?? "", style.FontFamily ?? "", Number(style.FontSize),
            YesNo(style.Bold), YesNo(style.Italic), YesNo(style.Underline),
            style.HAlign?.ToString() ?? "", style.VAlign?.ToString() ?? "",
            Colour(style.ForeColor), Colour(style.BackColor), Colour(style.BorderColor),
            Number(style.BorderWidth), Number(style.CornerRadius)
        };
    }

    /// <summary>
    /// Reads a row of cells (missing cells count as empty). Returns the style, or null with <paramref name="problem"/> in plain words
    /// when a cell cannot be understood (a number that is not a number, a Yes / No cell that is neither, an unknown alignment).
    /// A row with nothing in it gives null and no problem (an empty row).
    /// </summary>
    public static StyleDefinition? FromCells(IReadOnlyList<string?> cells, out string? problem, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string Cell(int i) => i < cells.Count ? (cells[i] ?? "").Trim() : "";

        problem = null;
        if (Enumerable.Range(0, Columns.Count).All(i => Cell(i).Length == 0)) return null;

        var name = Cell(NameColumn);
        string where = name.Length == 0 ? "A style" : $"Style '{name}'";
        var errors = new List<string>();

        bool? YesNo(int column)
        {
            var text = Cell(column);
            if (text.Length == 0) return null;
            if (text.Equals(Yes, StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals(No, StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            errors.Add($"{where}: {Columns[column]} must be Yes, No or empty.");
            return null;
        }

        double? Number(int column)
        {
            var text = Cell(column);
            if (text.Length == 0) return null;
            if (double.TryParse(text, NumberStyles.Float, culture, out var v) && double.IsFinite(v)) return v;
            errors.Add($"{where}: {Columns[column]} '{text}' is not a number.");
            return null;
        }

        string? Colour(int column)
        {
            var text = Cell(column);
            if (text.Length == 0) return null;
            return text.Equals(NoColour, StringComparison.OrdinalIgnoreCase) ? "" : text;
        }

        HAlign? Horizontal()
        {
            var text = Cell(AlignColumn);
            if (text.Length == 0) return null;
            if (Enum.TryParse<HAlign>(text, true, out var v) && Enum.IsDefined(v)) return v;
            errors.Add($"{where}: Align must be Left, Center, Right or empty.");
            return null;
        }

        VAlign? Vertical()
        {
            var text = Cell(VerticalColumn);
            if (text.Length == 0) return null;
            if (Enum.TryParse<VAlign>(text, true, out var v) && Enum.IsDefined(v)) return v;
            errors.Add($"{where}: Vertical must be Top, Middle, Bottom or empty.");
            return null;
        }

        var style = new StyleDefinition
        {
            Name = name,
            Parent = Cell(ParentColumn),
            FontFamily = Cell(FontColumn).Length == 0 ? null : Cell(FontColumn),
            FontSize = Number(SizeColumn),
            Bold = YesNo(BoldColumn),
            Italic = YesNo(ItalicColumn),
            Underline = YesNo(UnderlineColumn),
            HAlign = Horizontal(),
            VAlign = Vertical(),
            ForeColor = Colour(TextColourColumn),
            BackColor = Colour(FillColourColumn),
            BorderColor = Colour(BorderColourColumn),
            BorderWidth = Number(BorderWidthColumn),
            CornerRadius = Number(CornerColumn)
        };

        if (errors.Count > 0) { problem = errors[0]; return null; }
        return style;
    }
}
