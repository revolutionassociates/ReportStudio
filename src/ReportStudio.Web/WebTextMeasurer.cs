using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export.Pdf;

namespace ReportStudio.Web;

/// <summary>
/// Measures wrapped text for the web viewer without GDI+ (requirement 49): it reads the advance widths of the TrueType fonts shipped in the
/// "Fonts" folder and wraps words the way the HTML output does (the text box has 2 pixels of padding on each side, long words break anywhere,
/// a line is 1.25 times the font size high). A font that is not shipped is measured with the regular Ubuntu face, and a character the font
/// does not have counts as 0.55 of the font size. Safe to share between requests: nothing changes after construction.
/// </summary>
public sealed class WebTextMeasurer : ITextMeasurer
{
    private const double LineFactor = 1.25;
    private const double MissingGlyphEm = 0.55;
    private const double Padding = 4;

    private readonly Dictionary<string, TrueTypeFont> _faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrueTypeFont? _fallback;
    private readonly EstimatingTextMeasurer _estimate = new();

    /// <summary>Loads every .ttf in <paramref name="fontsFolder"/> (default: the "Fonts" folder next to the program). A damaged font is skipped.</summary>
    public WebTextMeasurer(string? fontsFolder = null)
    {
        fontsFolder ??= Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (Directory.Exists(fontsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(fontsFolder, "*.ttf").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var font = TrueTypeFont.Load(file);
                    _ = font.GlyphIndex(' ');   // builds the character map now, so sharing the font between requests needs no lock
                    _faces[Key(font.FamilyName, font.IsBold, font.IsItalic)] = font;
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // a font that cannot be read is simply not used
                }
            }
        }
        _fallback = _faces.GetValueOrDefault(Key(ReportItem.DefaultFontFamily, false, false)) ?? _faces.Values.FirstOrDefault();
    }

    /// <summary>How many fonts were loaded.</summary>
    public int FontCount => _faces.Count;

    private static string Key(string family, bool bold, bool italic) => $"{family}|{(bold ? "b" : "")}{(italic ? "i" : "")}";

    private TrueTypeFont? Find(ReportItem item) =>
        _faces.GetValueOrDefault(Key(item.FontFamily, item.Bold, item.Italic))
        ?? _faces.GetValueOrDefault(Key(item.FontFamily, item.Bold, false))
        ?? _faces.GetValueOrDefault(Key(item.FontFamily, false, false))
        ?? _faces.GetValueOrDefault(Key(ReportItem.DefaultFontFamily, item.Bold, item.Italic))
        ?? _fallback;

    public double MeasureHeight(string text, ReportItem item, double width)
    {
        var font = Find(item);
        if (font == null) return _estimate.MeasureHeight(text, item, width);

        double px = item.FontSize * 96.0 / 72.0;
        double room = Math.Max(1, width - Padding);
        int lines = 0;
        foreach (var paragraph in (text ?? "").Replace("\r", "").Split('\n'))
            lines += Math.Max(1, CountLines(font, paragraph, px, room));
        return lines * px * LineFactor + 2;
    }

    /// <summary>The number of lines a paragraph (no line breaks) takes in a box <paramref name="room"/> pixels wide.</summary>
    internal static int CountLines(TrueTypeFont font, string paragraph, double px, double room)
    {
        if (paragraph.Length == 0) return 1;

        double space = Advance(font, ' ', px);
        int lines = 1;
        double used = 0;
        bool lineHasWord = false;

        foreach (var word in paragraph.Split(' '))
        {
            double w = Width(font, word, px);
            if (w > room)
            {
                // a word wider than the box breaks anywhere: it fills whole lines
                if (lineHasWord) { lines++; used = 0; }
                int extra = (int)Math.Ceiling(w / room) - 1;
                lines += extra;
                used = w - extra * room;
                lineHasWord = true;
                continue;
            }
            double needed = lineHasWord ? used + space + w : w;
            if (needed <= room || !lineHasWord)
            {
                used = needed;
            }
            else
            {
                lines++;
                used = w;
            }
            lineHasWord = true;
        }
        return lines;
    }

    private static double Width(TrueTypeFont font, string text, double px)
    {
        double total = 0;
        foreach (var rune in text.EnumerateRunes()) total += Advance(font, rune.Value, px);
        return total;
    }

    private static double Advance(TrueTypeFont font, int codePoint, double px)
    {
        if (!font.HasGlyph(codePoint)) return codePoint == ' ' ? px * 0.3 : px * MissingGlyphEm;
        return font.AdvanceUnits(font.GlyphIndex(codePoint)) * px / font.UnitsPerEm;
    }
}
