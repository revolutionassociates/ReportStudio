using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>Where and how files may be read.</summary>
public sealed class SourceSettings
{
    /// <summary>A relative file name is looked up in this folder (normally the report's own folder). Empty = the current folder.</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// When set, only files inside this folder can be read: for a host that runs reports written by other people. A link inside the folder
    /// that points outside it is refused. Empty = any file the user can read.
    /// </summary>
    public string? FileRoot { get; init; }

    /// <summary>Network paths (\\server\share) can leak the user's Windows credentials to whoever writes the path, so they are refused unless allowed.</summary>
    public bool AllowNetworkPaths { get; init; }
}

/// <summary>Reads the rows of a JSON, XML, CSV or Excel file (requirement 40).</summary>
public static class FileSourceLoader
{
    /// <summary>How many rows are read to find the field names.</summary>
    public const int SampleRows = 200;

    public static List<Row> Load(DataSourceDefinition ds, SourceSettings settings) => Read(ds, settings, SourceLimits.MaxRows, stopAtLimit: false);

    /// <summary>The field names of the first rows, in the order they first appear. For the designer's field list; reads no more than needed where it can.</summary>
    public static List<string> GetFieldNames(DataSourceDefinition ds, SourceSettings settings)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Read(ds, settings, SampleRows, stopAtLimit: true).Take(SampleRows))
            foreach (var name in row.Keys)
                if (seen.Add(name)) names.Add(name);
        return names;
    }

    /// <summary>Checks the file can be found and read; throws a message the user can act on.</summary>
    public static void Test(DataSourceDefinition ds, SourceSettings settings) => Read(ds, settings, 1, stopAtLimit: true);

    private static List<Row> Read(DataSourceDefinition ds, SourceSettings settings, int maxRows, bool stopAtLimit)
    {
        if (!ds.Provider.IsFile()) throw new InvalidOperationException($"{ds.Provider} is not a file source.");
        var path = ResolvePath(ds.Source, settings);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > SourceLimits.MaxBytes)
                throw new SourceException($"The file is larger than the {SourceLimits.MaxBytes / (1024 * 1024)} MB that can be read.");

            return ds.Provider switch
            {
                DbProvider.Json => JsonSource.Read(stream, ds.Selector, stopAtLimit ? int.MaxValue : maxRows),
                DbProvider.Xml => XmlSource.Read(stream, ds.Selector, stopAtLimit ? int.MaxValue : maxRows),
                DbProvider.Csv => CsvSource.Read(stream, ds.Delimiter, ds.HasHeaderRow, maxRows, stopAtLimit),
                DbProvider.Excel => ExcelSource.Read(stream, ds.Selector, ds.HasHeaderRow, maxRows, stopAtLimit),
                _ => throw new InvalidOperationException($"{ds.Provider} is not a file source.")
            };
        }
        catch (UnauthorizedAccessException)
        {
            throw new SourceException($"The file '{Path.GetFileName(path)}' cannot be read: access is denied.");
        }
        catch (IOException ex)
        {
            throw new SourceException($"The file '{Path.GetFileName(path)}' cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The full path of the file a report names, after the rules above. Throws <see cref="SourceException"/> when it may not or cannot be read.</summary>
    public static string ResolvePath(string? source, SourceSettings settings)
    {
        var name = (source ?? "").Trim();
        if (name.Length == 0) throw new SourceException("The data source has no file.");
        if (name.Contains('\0')) throw new SourceException("The file name is not valid.");
        if (name.Contains("://", StringComparison.Ordinal))
            throw new SourceException("A file source needs a file name, not a web address. Use the REST or OData source for an address.");
        if (IsNetworkPath(name) && !settings.AllowNetworkPaths)
            throw new SourceException("Network paths (\\\\server\\share) are not allowed for data files. Copy the file to a local folder, or allow network paths.");

        string full;
        try
        {
            var baseDirectory = string.IsNullOrWhiteSpace(settings.BaseDirectory) ? Directory.GetCurrentDirectory() : settings.BaseDirectory;
            full = Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(baseDirectory, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SourceException("The file name is not valid.", ex);
        }
        if (IsNetworkPath(full) && !settings.AllowNetworkPaths)
            throw new SourceException("Network paths (\\\\server\\share) are not allowed for data files.");

        var info = new FileInfo(full);
        if (!info.Exists) throw new SourceException($"The data file '{name}' was not found.");

        if (!string.IsNullOrWhiteSpace(settings.FileRoot))
        {
            var real = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
            var root = Path.GetFullPath(settings.FileRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!real.StartsWith(root, comparison))
                throw new SourceException($"The data file '{name}' is outside the folder reports may read from.");
        }
        return full;
    }

    private static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
