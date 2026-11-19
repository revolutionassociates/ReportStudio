using System.Text.Json;

namespace ReportStudio.Core.Ui;

/// <summary>
/// What the designer remembers between runs (requirement 47): the colour theme and the language. Kept in a small JSON file under the
/// user's application data folder. A missing, damaged or oversized file just gives the defaults, so it can never stop the designer from starting.
/// </summary>
public sealed class DesignerSettings
{
    public const string ThemeLight = "Light";
    public const string ThemeDark = "Dark";
    public const string ThemeSystem = "System";

    /// <summary>The biggest settings file that is read.</summary>
    public const long MaxFileBytes = 64 * 1024;

    /// <summary>Light, Dark, or System (follow Windows).</summary>
    public string Theme { get; set; } = ThemeSystem;

    /// <summary>The code of the language ("en" = English).</summary>
    public string Language { get; set; } = "en";

    /// <summary>True to show the toolbar as ribbon tabs (Home, Insert, Format, View); false for the single toolbar.</summary>
    public bool Ribbon { get; set; }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReportStudio");

    public static string DefaultPath => Path.Combine(DefaultFolder, "settings.json");

    /// <summary>Where the language files are kept.</summary>
    public static string LanguageFolder => Path.Combine(DefaultFolder, "Languages");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Puts a theme name right: one of the three known names (without regard to case), otherwise System.</summary>
    public static string NormalizeTheme(string? theme) =>
        new[] { ThemeLight, ThemeDark, ThemeSystem }.FirstOrDefault(t => string.Equals(t, theme?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ThemeSystem;

    /// <summary>
    /// The palette to use: the one chosen, or for System the one that matches <paramref name="systemIsDark"/> (what Windows is set to).
    /// </summary>
    public UiPalette Palette(bool systemIsDark)
    {
        var theme = NormalizeTheme(Theme);
        if (theme == ThemeSystem) return systemIsDark ? UiPalette.Dark : UiPalette.Light;
        return UiPalette.ByName(theme);
    }

    public static DesignerSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return new DesignerSettings();
            var loaded = JsonSerializer.Deserialize<DesignerSettings>(File.ReadAllText(path), Json) ?? new DesignerSettings();
            loaded.Theme = NormalizeTheme(loaded.Theme);
            loaded.Language = string.IsNullOrWhiteSpace(loaded.Language) ? "en" : loaded.Language.Trim();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesignerSettings();
        }
    }

    /// <summary>Saves the settings. Returns false (and changes nothing else) when the file cannot be written.</summary>
    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
