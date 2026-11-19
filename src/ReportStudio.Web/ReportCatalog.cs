using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReportStudio.Core.Model;

namespace ReportStudio.Web;

/// <summary>One report offered by the web viewer: the file name without ".rsr" (also its address) and the title to show.</summary>
public sealed record ReportEntry(string Id, string Title);

/// <summary>
/// The reports folder of the web viewer. A report is named by its file name only: anything that could point outside the folder
/// (a path, "..", a drive, a link to another place) is not a report, so a visitor cannot reach any other file through the address.
/// </summary>
public sealed class ReportCatalog
{
    public const int MaxEntries = 1000;
    public const int MaxIdLength = 100;

    private readonly IOptions<WebViewerOptions> _options;
    private readonly ConcurrentDictionary<string, (DateTime Stamp, string Title)> _titles = new(StringComparer.OrdinalIgnoreCase);

    public ReportCatalog(IOptions<WebViewerOptions> options) => _options = options;

    /// <summary>The full path of the reports folder, without a closing separator.</summary>
    public string Folder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.Value.ReportsFolder));

    /// <summary>A name that can be one report file: letters, digits, space and - _ . ( ) only, no leading dot, not longer than <see cref="MaxIdLength"/>.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || id[0] == '.' || id[0] == ' ' || id[^1] == ' ' || id[^1] == '.') return false;
        if (id.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in id)
            if (!(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')')) return false;
        return true;
    }

    public IReadOnlyList<ReportEntry> List()
    {
        var folder = Folder;
        if (!Directory.Exists(folder)) return Array.Empty<ReportEntry>();

        var list = new List<ReportEntry>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.rsr").Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id) || Resolve(id) is not { } path) continue;
            list.Add(new ReportEntry(id, TitleOf(path, id)));
            if (list.Count >= MaxEntries) break;
        }
        return list;
    }

    /// <summary>The full path of the report with this name, or null when there is no such report (or the name is not allowed).</summary>
    public string? Resolve(string? id)
    {
        if (!IsValidId(id)) return null;
        var folder = Folder;
        var path = Path.GetFullPath(Path.Combine(folder, id + ".rsr"));
        if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase)) return null;

        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget != null) return null;     // a link could lead anywhere
        return path;
    }

    /// <summary>Reads the report file. Throws IOException, JsonException, InvalidDataException or UnauthorizedAccessException when it cannot be read.</summary>
    public ReportDefinition Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > (long)_options.Value.MaxReportFileKb * 1024)
            throw new InvalidDataException("The report file is bigger than this server allows.");
        return ReportDefinition.Load(path);
    }

    private string TitleOf(string path, string id)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_titles.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Title;

            var def = Load(path);
            var title = !string.IsNullOrWhiteSpace(def.Title) ? def.Title.Trim() : !string.IsNullOrWhiteSpace(def.Name) ? def.Name.Trim() : id;
            _titles[path] = (stamp, title);
            return title;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return id;
        }
    }
}
