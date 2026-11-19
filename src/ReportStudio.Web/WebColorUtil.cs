using System.Drawing;

namespace ReportStudio.Export;

/// <summary>
/// The web viewer's stand-in for the Export project's ColorUtil (which sits in the GDI+ renderer's file): the linked HtmlExporter only needs
/// the red, green and blue of a report colour. Color is the plain colour struct from the base library, so nothing here needs GDI+ or Windows.
/// </summary>
internal static class ColorUtil
{
    /// <summary>Parses "#RRGGBB", "#AARRGGBB" or a colour name. Empty or invalid text returns null (transparent).</summary>
    public static Color? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        try
        {
            if (text.StartsWith('#'))
            {
                var hex = text[1..];
                if (hex.Length == 6) return Color.FromArgb(Convert.ToInt32(hex, 16) | unchecked((int)0xFF000000));
                if (hex.Length == 8) return Color.FromArgb(unchecked((int)Convert.ToUInt32(hex, 16)));
                return null;
            }
            var named = Color.FromName(text);
            return named.IsKnownColor ? named : null;
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }
}
