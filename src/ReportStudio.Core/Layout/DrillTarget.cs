namespace ReportStudio.Core.Layout;

/// <summary>Finds the report a drill-through item opens. Only a .rsr file inside the report's own folder is allowed.</summary>
public static class DrillTarget
{
    /// <summary>
    /// The full path of the report named <paramref name="name"/> (relative to <paramref name="baseDirectory"/>), or null when
    /// it is empty, not a .rsr file, or would lead outside the folder (.., absolute paths, other drives, network paths).
    /// The file may not exist: the caller checks.
    /// </summary>
    public static string? ResolveFile(string? baseDirectory, string? name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(baseDirectory) || name.Length == 0 || name.Any(char.IsControl)) return null;
        if (!name.EndsWith(".rsr", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var root = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.IsPathRooted(name)) return null;
            var full = Path.GetFullPath(Path.Combine(root, name));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
