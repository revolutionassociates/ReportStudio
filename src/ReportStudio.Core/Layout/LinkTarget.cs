using System.Text;

namespace ReportStudio.Core.Layout;

public enum LinkKind { None, External, Internal }

/// <summary>
/// Checks and cleans the address of a hyperlink (requirement 27). A report may only link to a web page (http, https),
/// an e-mail address (mailto), a phone number (tel) or a bookmark in the same report (#Name). Anything else, such as
/// file:, javascript: or a network path, is refused, because a report is often opened from a file somebody else wrote.
/// </summary>
public static class LinkTarget
{
    private const int MaxLength = 2000;
    private static readonly string[] Schemes = { "http", "https", "mailto", "tel" };

    /// <summary>The cleaned address and what kind it is; <see cref="LinkKind.None"/> (and "") when empty or not allowed.</summary>
    public static (LinkKind Kind, string Value) Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s.Length > MaxLength || s.Any(char.IsControl)) return (LinkKind.None, "");

        if (s[0] == '#')
        {
            var name = s.Substring(1).Trim();
            return name.Length == 0 ? (LinkKind.None, "") : (LinkKind.Internal, "#" + name);
        }

        int colon = s.IndexOf(':');
        if (colon <= 0) return (LinkKind.None, "");
        var scheme = s.Substring(0, colon).ToLowerInvariant();
        if (!Schemes.Contains(scheme)) return (LinkKind.None, "");

        var rest = s.Substring(colon + 1);
        if (scheme is "http" or "https")
        {
            if (!rest.StartsWith("//", StringComparison.Ordinal)) return (LinkKind.None, "");
            var host = rest.Substring(2).TrimStart('/');
            if (host.Length == 0 || host[0] is '?' or '#') return (LinkKind.None, "");
        }
        else if (rest.Length == 0) return (LinkKind.None, "");

        return (LinkKind.External, scheme + ":" + Escape(rest));
    }

    /// <summary>The cleaned address, or "" when empty or not allowed.</summary>
    public static string Clean(string? text) => Parse(text).Value;

    /// <summary>Percent-encodes what an address cannot hold (spaces, accents, quotes); characters already in use stay.</summary>
    private static string Escape(string text)
    {
        const string keep = "-._~:/?#[]@!$&'()*+,;=%";
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            char c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || keep.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
