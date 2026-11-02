using System.Runtime.CompilerServices;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// Finds the font file for a report font: the application's Fonts folder first, then the fonts installed in
/// Windows, then the default family. A report never fails because a font is missing; it prints in the default font.
/// </summary>
internal static class PdfFontProvider
{
    private sealed record SystemFace(string Family, bool Bold, bool Italic, string Path);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TrueTypeFont> Resolved = new();
    private static readonly ConditionalWeakTable<byte[], TrueTypeFont> Parsed = new();
    private static readonly Dictionary<string, TrueTypeFont> FromFiles = new(StringComparer.OrdinalIgnoreCase);
    private static List<SystemFace>? _system;

    /// <summary>Forget earlier answers: the catalog got new fonts, so a family that fell back may exist now.</summary>
    public static void Reset()
    {
        lock (Gate) Resolved.Clear();
    }

    public static TrueTypeFont Resolve(string family, bool bold, bool italic)
    {
        lock (Gate)
        {
            var key = FontCatalog.Key(family ?? "", bold, italic);
            if (Resolved.TryGetValue(key, out var hit)) return hit;

            var font = FromCatalog(family ?? "", bold, italic)
                       ?? FromSystem(family ?? "", bold, italic)
                       ?? FromCatalog(FontCatalog.DefaultFamily, bold, italic)
                       ?? FromSystem("Arial", bold, italic)
                       ?? throw new InvalidOperationException(
                           "No font is available for the PDF. Put the Ubuntu font files in the Fonts folder next to the program (see fonts/README.txt).");
            Resolved[key] = font;
            return font;
        }
    }

    private static TrueTypeFont? FromCatalog(string family, bool bold, bool italic)
    {
        var bytes = FontCatalog.GetFace(family, bold, italic);
        if (bytes == null) return null;
        try { return Parsed.GetValue(bytes, TrueTypeFont.Load); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { return null; }
    }

    private static TrueTypeFont? FromSystem(string family, bool bold, bool italic)
    {
        _system ??= ScanSystemFonts();
        var same = _system.Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
        var face = same.FirstOrDefault(f => f.Bold == bold && f.Italic == italic)
                   ?? same.FirstOrDefault(f => !f.Bold && !f.Italic)
                   ?? same.FirstOrDefault();
        if (face == null) return null;

        if (FromFiles.TryGetValue(face.Path, out var cached)) return cached;
        try { return FromFiles[face.Path] = TrueTypeFont.Load(face.Path); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Reads the names of the installed .ttf files once. Collections (.ttc) and PostScript fonts are skipped.</summary>
    private static List<SystemFace> ScanSystemFonts()
    {
        var list = new List<SystemFace>();
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;

        foreach (var file in Directory.EnumerateFiles(folder, "*.ttf"))
        {
            try
            {
                var font = TrueTypeFont.Load(file);
                if (font.FamilyName.Length > 0) list.Add(new SystemFace(font.FamilyName, font.IsBold, font.IsItalic, file));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // not a usable font file: ignore it
            }
        }
        return list;
    }
}
