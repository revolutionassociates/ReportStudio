using System.Globalization;

namespace ReportStudio.Core.Ui;

/// <summary>
/// The colours of the designer's windows (requirement 47): a light and a dark set. Colours are #RRGGBB text so this stays free of any
/// drawing library; the designer turns them into its own colours. <see cref="Check"/> and <see cref="ContrastRatio"/> keep a palette readable.
/// </summary>
public sealed record UiPalette(
    string Name,
    string Back,         // windows, panels, menus
    string Fore,         // normal text
    string Muted,        // hints, disabled text
    string Input,        // the inside of text boxes, lists and grids
    string Border,       // lines and frames
    string Accent,       // the selected item and the main button
    string AccentText,   // text on the accent colour
    string HeaderBack)   // grid headings and tool bars
{
    public static UiPalette Light { get; } = new("Light",
        Back: "#F3F3F3", Fore: "#1B1B1B", Muted: "#5F5F5F", Input: "#FFFFFF", Border: "#C8C8C8",
        Accent: "#0B5CAD", AccentText: "#FFFFFF", HeaderBack: "#E6E6E6");

    public static UiPalette Dark { get; } = new("Dark",
        Back: "#202124", Fore: "#E8EAED", Muted: "#9AA0A6", Input: "#2B2C30", Border: "#4A4C52",
        Accent: "#5AA2E8", AccentText: "#0B1B2B", HeaderBack: "#2D2E33");

    public static IReadOnlyList<UiPalette> All { get; } = new[] { Light, Dark };

    /// <summary>The palette of that name (without regard to case); the light one when the name is not known.</summary>
    public static UiPalette ByName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Light;

    private IEnumerable<(string What, string Value)> Colours => new[]
    {
        ("Back", Back), ("Fore", Fore), ("Muted", Muted), ("Input", Input), ("Border", Border),
        ("Accent", Accent), ("AccentText", AccentText), ("HeaderBack", HeaderBack)
    };

    /// <summary>#RRGGBB text as its red, green and blue parts, or null when it is not that.</summary>
    public static (int R, int G, int B)? Rgb(string? hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return null;
        if (!int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return null;
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    /// <summary>How well two colours stand out from each other, from 1 (the same) to 21 (black on white). Null when a colour is not #RRGGBB.</summary>
    public static double? ContrastRatio(string foreground, string background)
    {
        if (Rgb(foreground) is not { } f || Rgb(background) is not { } b) return null;
        double hi = Math.Max(Luminance(f), Luminance(b)), lo = Math.Min(Luminance(f), Luminance(b));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Problems in plain sentences: a colour that is not #RRGGBB, text that is hard to read on its background.</summary>
    public List<string> Check()
    {
        var problems = new List<string>();
        foreach (var (what, value) in Colours)
            if (Rgb(value) == null) problems.Add($"{Name}: {what} '{value}' is not a colour written as #RRGGBB.");
        if (problems.Count > 0) return problems;

        void NeedsContrast(string what, string fore, string back, double minimum)
        {
            var ratio = ContrastRatio(fore, back)!.Value;
            if (ratio < minimum) problems.Add($"{Name}: {what} is hard to read (contrast {ratio:0.0}, at least {minimum:0.#} is needed).");
        }
        NeedsContrast("text on the window", Fore, Back, 4.5);
        NeedsContrast("text in boxes and lists", Fore, Input, 4.5);
        NeedsContrast("text on the grid headings", Fore, HeaderBack, 4.5);
        NeedsContrast("hints", Muted, Back, 3);
        NeedsContrast("text on the selected item", AccentText, Accent, 4.5);
        return problems;
    }
}
