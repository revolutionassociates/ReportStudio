namespace ReportStudio.Web.Tests;

/// <summary>Where the tests find the shipped fonts, and a temp folder for each test.</summary>
internal static class TestFiles
{
    /// <summary>The repository's "fonts" folder, found by walking up from the test program.</summary>
    public static string FontsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "fonts");
            if (File.Exists(Path.Combine(candidate, "Ubuntu-Regular.ttf"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("The fonts folder was not found above " + AppContext.BaseDirectory);
    }

    public static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "rs-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder does no harm */ }
        catch (UnauthorizedAccessException) { }
    }
}
