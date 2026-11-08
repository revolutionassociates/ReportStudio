using System.Text;
using System.Text.RegularExpressions;

namespace ReportStudio.Core.Layout;

/// <summary>
/// Turns a value from a data field into picture bytes: raw bytes (a database image column), base64 text, a
/// <c>data:image/png;base64,...</c> address, or SVG markup. Only bytes that really start like a picture (or a plain, safe SVG drawing)
/// are accepted, so a wrong column prints nothing and a warning instead of a broken image.
/// </summary>
public static class ImageData
{
    /// <summary>Largest picture taken from a field (guards memory and the report file size).</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>Largest SVG drawing accepted (text files are small; a huge one is not a drawing).</summary>
    public const int MaxSvgBytes = 5 * 1024 * 1024;

    private static readonly Regex SvgStart = new(@"^(<\?xml[^>]*\?>\s*)?(<!--.*?-->\s*)*<svg[\s>]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Things an SVG may contain that reach outside the drawing: scripts, entities, other files and web addresses.
    private static readonly Regex SvgUnsafe = new(
        @"<!DOCTYPE|<!ENTITY|<script|<foreignObject|<iframe|<embed|<object|<audio|<video|@import|javascript:|\bon[a-z]+\s*=|" +
        @"(xlink:)?href\s*=\s*([""'])(?>\s*)(?!#|data:image/)|url\((?>\s*)(?>[""']?)(?>\s*)(?!#)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DataUri = new(@"^data:image/[a-z0-9.+-]+;base64,", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the bytes start like a PNG, JPEG, GIF, BMP or TIFF picture.</summary>
    public static bool LooksLikePicture(byte[]? b)
    {
        if (b == null || b.Length < 4 || b.Length > MaxBytes) return false;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;     // PNG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;                      // JPEG
        if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return true;          // GIF
        if (b[0] == 'B' && b[1] == 'M') return true;                                        // BMP
        if ((b[0] == 'I' && b[1] == 'I' && b[2] == 0x2A && b[3] == 0) ||
            (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 0x2A)) return true;         // TIFF
        return false;
    }

    /// <summary>True when the bytes are text that starts like an SVG drawing (an optional XML line and comments, then &lt;svg).</summary>
    public static bool LooksLikeSvg(byte[]? b)
    {
        if (b == null || b.Length < 6 || b.Length > MaxSvgBytes) return false;
        int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;          // UTF-8 byte order mark
        var head = Encoding.UTF8.GetString(b, skip, Math.Min(b.Length - skip, 2048)).TrimStart();
        return SvgStart.IsMatch(head);
    }

    /// <summary>
    /// True when an SVG has nothing that reaches outside the drawing: no scripts, entities, event handlers, web or file addresses
    /// (links to a place inside it, #name, and pictures held in the file itself, data:image/, are fine).
    /// </summary>
    public static bool IsSafeSvg(byte[]? b)
    {
        if (!LooksLikeSvg(b)) return false;
        int skip = b!.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return !SvgUnsafe.IsMatch(Encoding.UTF8.GetString(b, skip, b.Length - skip));
    }

    /// <summary>A picture the report accepts: a raster picture, or a safe SVG drawing.</summary>
    public static bool IsPicture(byte[]? b) => LooksLikePicture(b) || IsSafeSvg(b);

    /// <summary>
    /// The picture held by a field value, or null when there is none (null, empty, text that is not base64, or bytes that are not a picture).
    /// </summary>
    public static byte[]? FromValue(object? value)
    {
        switch (value)
        {
            case null: return null;
            case byte[] bytes: return IsPicture(bytes) ? bytes : null;
            case string text: return FromText(text);
            default: return null;
        }
    }

    /// <summary>Base64 text, with or without the <c>data:image/...;base64,</c> start and with line breaks allowed.</summary>
    public static byte[]? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('<'))                                       // SVG markup held in the field itself
        {
            var markup = Encoding.UTF8.GetBytes(s);
            return IsPicture(markup) ? markup : null;
        }
        var m = DataUri.Match(s);
        if (m.Success) s = s[m.Length..];
        if (s.Length < 8 || s.Length > MaxBytes * 4L / 3 + 8) return null;
        s = s.Replace("\r", "").Replace("\n", "").Replace(" ", "");
        try
        {
            var bytes = Convert.FromBase64String(s);
            return IsPicture(bytes) ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// When the whole text is one <c>{expression}</c> (nothing else around it), the expression; otherwise null.
    /// Such a text can hand over raw bytes instead of text.
    /// </summary>
    public static string? SingleExpression(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var t = template.Trim();
        if (t.Length < 3 || t[0] != '{' || t[^1] != '}') return null;
        var inner = t[1..^1];
        return inner.Contains('{') || inner.Contains('}') || string.IsNullOrWhiteSpace(inner) ? null : inner.Trim();
    }
}
