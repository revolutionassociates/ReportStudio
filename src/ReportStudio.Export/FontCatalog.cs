using System.Drawing;
using System.Drawing.Text;
using ReportStudio.Export.Pdf;

namespace ReportStudio.Export;

/// <summary>
/// Fonts shipped with the application (TrueType files in a "Fonts" folder next to the program). They are used
/// for measuring, drawing and image export (GDI+), and for the PDF, where every font used is embedded
/// so the document looks the same on any machine.
/// File names decide family and style: "Ubuntu-Regular.ttf", "Ubuntu-Bold.ttf", "Ubuntu-Italic.ttf",
/// "Ubuntu-BoldItalic.ttf". Call <see cref="AddDirectory"/> to load more folders.
/// </summary>
public static class FontCatalog
{
    /// <summary>The default report font, also the PDF fallback for a font that is not in the catalog.</summary>
    public const string DefaultFamily = "Ubuntu";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]> Faces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly PrivateFontCollection Collection = new();
    private static bool _initialized;

    public static bool HasFonts
    {
        get { Initialize(); lock (Gate) return Faces.Count > 0; }
    }

    /// <summary>Loads the default "Fonts" folder once. Safe to call repeatedly.</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
            LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Fonts"));
        }
    }

    /// <summary>Loads every .ttf in a folder (for example a customer's own fonts).</summary>
    public static void AddDirectory(string directory)
    {
        Initialize();
        lock (Gate) LoadDirectory(directory);
        PdfFontProvider.Reset();                   // a family that fell back to the default may exist now
    }

    /// <summary>A catalog family (e.g. Ubuntu) for GDI+, or null when it was not loaded.</summary>
    public static FontFamily? FindFamily(string name)
    {
        Initialize();
        lock (Gate)
            return Collection.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static byte[]? GetFace(string family, bool bold, bool italic)
    {
        Initialize();
        lock (Gate)
        {
            if (Faces.TryGetValue(Key(family, bold, italic), out var exact)) return exact;
            // A missing bold/italic file falls back to the regular face of the same family.
            return Faces.TryGetValue(Key(family, false, false), out var regular) ? regular : null;
        }
    }

    internal static string Key(string family, bool bold, bool italic) => $"{family}|{(bold ? "b" : "")}{(italic ? "i" : "")}";

    private static void LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.ttf"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            int dash = name.LastIndexOf('-');
            var family = (dash > 0 ? name[..dash] : name).Trim();
            var style = dash > 0 ? name[(dash + 1)..] : "";
            bool bold = style.Contains("bold", StringComparison.OrdinalIgnoreCase);
            bool italic = style.Contains("italic", StringComparison.OrdinalIgnoreCase)
                          || style.Contains("oblique", StringComparison.OrdinalIgnoreCase);

            try
            {
                Faces[Key(family, bold, italic)] = File.ReadAllBytes(file);
                Collection.AddFontFile(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                // an unreadable or damaged font file is skipped; the report falls back to another font
            }
        }
    }
}
