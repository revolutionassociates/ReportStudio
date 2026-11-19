using System.Globalization;
using System.Text;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Wizard;

/// <summary>
/// The person's own report templates (requirement 45c): ordinary .rsr files in one folder, listed next to the built-in templates under
/// "My templates". The file name is the template name, so a template can also be opened, copied or removed in the file explorer.
/// </summary>
public static class UserTemplates
{
    public const string Category = "My templates";

    private const string IdPrefix = "user:";
    private const string Extension = ".rsr";
    private const int MaxNameLength = 80;

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Where the designer keeps the templates: ReportStudio\Templates under the user's application data folder.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReportStudio", "Templates");

    /// <summary>
    /// The file name (without extension) a template name is saved under: characters a file name cannot hold become "_", the length is
    /// limited, and names Windows reserves ("CON", "COM1" ...) get a "_" added. Throws <see cref="ArgumentException"/> when nothing is left.
    /// </summary>
    public static string FileNameFor(string? name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim()) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);

        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd('.', ' ');
        if (clean.Length == 0 || clean.All(c => c == '_'))
            throw new ArgumentException("Give the template a name.", nameof(name));
        if (ReservedNames.Contains(clean, StringComparer.OrdinalIgnoreCase)) clean += "_";
        return clean;
    }

    /// <summary>Saves the report as a template (a template of the same name is replaced) and returns the file written.</summary>
    public static string Save(string folder, ReportDefinition report, string name)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var path = PathFor(folder, name);
        Directory.CreateDirectory(folder);
        report.Save(path);
        return path;
    }

    public static bool Exists(string folder, string name) => File.Exists(PathFor(folder, name));

    /// <summary>Removes a template. False when there was none of that name.</summary>
    public static bool Delete(string folder, string name)
    {
        var path = PathFor(folder, name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The templates in the folder by name. A folder that does not exist has none. A file is only read when the template is created.</summary>
    public static IReadOnlyList<ReportTemplate> List(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<ReportTemplate>();

        return Directory.EnumerateFiles(folder, "*" + Extension)
            .Where(f => string.Equals(Path.GetExtension(f), Extension, StringComparison.OrdinalIgnoreCase))
            .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ReportTemplate(
                IdPrefix + t.Name, t.Name, Category,
                "Your own template, saved " + System.IO.File.GetLastWriteTime(t.File).ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) + ".",
                () => ReportDefinition.Load(t.File)))
            .ToList();
    }

    /// <summary>True for a template made by <see cref="Save"/>, false for a built-in one.</summary>
    public static bool IsUser(ReportTemplate template) =>
        template.Id.StartsWith(IdPrefix, StringComparison.Ordinal);

    private static string PathFor(string folder, string name) =>
        Path.Combine(folder, FileNameFor(name) + Extension);
}
