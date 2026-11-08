using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportStudio.Core.Text;

/// <summary>A stretch of text with one look. The item's own font, size and colour apply wherever a run does not change them.</summary>
public sealed record TextRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, string Color = "");

/// <summary>
/// The small markup subset of a rich text element: &lt;b&gt; &lt;i&gt; &lt;u&gt; &lt;s&gt; (also strong, em, ins, strike, del),
/// &lt;br&gt;, &lt;p&gt; and &lt;div&gt; (a line break), &lt;font color="#RRGGBB"&gt; and &lt;span style="color:#RRGGBB; font-weight:bold;
/// font-style:italic; text-decoration:underline line-through"&gt;. Anything else is dropped and its text kept, so the result can never carry
/// a script, a link or a picture. Entities (&amp;lt; &amp;amp; &amp;#65;) are decoded. Line breaks in the text are kept as they are.
/// </summary>
public static class RichText
{
    /// <summary>Longest markup read; the rest is ignored.</summary>
    public const int MaxLength = 100_000;

    private sealed record Style(bool Bold, bool Italic, bool Underline, bool Strike, string Color)
    {
        public static readonly Style Plain = new(false, false, false, false, "");
    }

    private static readonly Regex TagName = new(@"^(/?)([A-Za-z][A-Za-z0-9]*)", RegexOptions.Compiled);       // no space after "<": "a < b" is text
    private static readonly Regex AttributePair = new(@"([A-Za-z][A-Za-z0-9-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))", RegexOptions.Compiled);
    private static readonly Regex Entity = new(@"&(#x[0-9A-Fa-f]{1,6}|#[0-9]{1,7}|[A-Za-z]{2,8});", RegexOptions.Compiled);
    private static readonly Regex HexColor = new(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000", ["white"] = "#FFFFFF", ["red"] = "#FF0000", ["green"] = "#008000", ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00", ["orange"] = "#FFA500", ["purple"] = "#800080", ["gray"] = "#808080", ["grey"] = "#808080",
        ["brown"] = "#A52A2A", ["navy"] = "#000080", ["teal"] = "#008080", ["maroon"] = "#800000"
    };

    /// <summary>True when any run changes the look (so a plain draw would lose something).</summary>
    public static bool IsStyled(IEnumerable<TextRun> runs) =>
        runs.Any(r => r.Bold || r.Italic || r.Underline || r.Strike || r.Color.Length > 0);

    /// <summary>The text of the runs with no markup.</summary>
    public static string Plain(IEnumerable<TextRun> runs) => string.Concat(runs.Select(r => r.Text));

    /// <summary>Makes a data value safe to put inside markup: &amp;, &lt; and &gt; become entities, so a value is never read as a tag.</summary>
    public static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>A colour as #RRGGBB (upper case) from #RGB, #RRGGBB or one of a few names; null when it is not one of those.</summary>
    public static string? NormalizeColor(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        if (NamedColors.TryGetValue(s, out var named)) return named;
        if (!HexColor.IsMatch(s)) return null;
        var hex = s[1..].ToUpperInvariant();
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        return "#" + hex;
    }

    /// <summary>Reads the markup into runs. Never throws; text that is not markup comes back as one plain run.</summary>
    public static List<TextRun> Parse(string? markup)
    {
        var runs = new List<TextRun>();
        if (string.IsNullOrEmpty(markup)) return runs;
        if (markup.Length > MaxLength) markup = markup[..MaxLength];
        markup = markup.Replace("\r\n", "\n").Replace('\r', '\n');

        var style = Style.Plain;
        var frames = new List<(string Name, Style Previous)>();
        var buffer = new StringBuilder();      // decoded text in the current look
        var raw = new StringBuilder();         // text read but not yet decoded

        void PushRaw()
        {
            if (raw.Length == 0) return;
            buffer.Append(Decode(raw.ToString()));
            raw.Clear();
        }
        void Flush()
        {
            PushRaw();
            if (buffer.Length == 0) return;
            Add(runs, buffer.ToString(), style);
            buffer.Clear();
        }
        void EnsureLineBreak()
        {
            PushRaw();
            bool any = buffer.Length > 0 || runs.Count > 0;
            bool broken = buffer.Length > 0 ? buffer[^1] == '\n' : runs.Count == 0 || runs[^1].Text.EndsWith('\n');
            if (any && !broken) buffer.Append('\n');
        }

        int i = 0;
        while (i < markup.Length)
        {
            bool closing, selfClosing;
            string name, attributes;
            int end;
            if (markup[i] != '<' || !TryReadTag(markup, i, out closing, out name, out attributes, out selfClosing, out end))
            {
                raw.Append(markup[i++]);
                continue;
            }
            i = end;
            PushRaw();

            switch (name)
            {
                case "br":
                    buffer.Append('\n');
                    break;
                case "p" or "div":
                    EnsureLineBreak();
                    break;
                case "b" or "i" or "u" or "s" or "font" or "span":
                    if (closing)
                    {
                        int at = frames.FindLastIndex(f => f.Name == name);
                        if (at < 0) break;
                        Flush();
                        style = frames[at].Previous;
                        frames.RemoveRange(at, frames.Count - at);
                    }
                    else if (!selfClosing)
                    {
                        Flush();
                        frames.Add((name, style));
                        style = Open(name, attributes, style);
                    }
                    break;
                // anything else: the tag is dropped, its text stays
            }
        }
        Flush();
        return runs;
    }

    private static Style Open(string name, string attributes, Style style)
    {
        switch (name)
        {
            case "b": return style with { Bold = true };
            case "i": return style with { Italic = true };
            case "u": return style with { Underline = true };
            case "s": return style with { Strike = true };
        }

        foreach (Match m in AttributePair.Matches(attributes))
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            if (name == "font" && key == "color")
            {
                var color = NormalizeColor(value);
                if (color != null) style = style with { Color = color };
            }
            else if (name == "span" && key == "style")
            {
                style = ApplyCss(value, style);
            }
        }
        return style;
    }

    private static Style ApplyCss(string css, Style style)
    {
        foreach (var declaration in css.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon < 0) continue;
            var key = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim().ToLowerInvariant();
            switch (key)
            {
                case "color":
                    var color = NormalizeColor(value);
                    if (color != null) style = style with { Color = color };
                    break;
                case "font-weight":
                    if (value == "bold" || value == "bolder" || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) && weight >= 600))
                        style = style with { Bold = true };
                    break;
                case "font-style":
                    if (value == "italic" || value == "oblique") style = style with { Italic = true };
                    break;
                case "text-decoration":
                    if (value.Contains("underline")) style = style with { Underline = true };
                    if (value.Contains("line-through")) style = style with { Strike = true };
                    break;
            }
        }
        return style;
    }

    private static bool TryReadTag(string text, int start, out bool closing, out string name, out string attributes, out bool selfClosing, out int end)
    {
        closing = selfClosing = false; name = attributes = ""; end = start;
        int gt = text.IndexOf('>', start + 1);
        if (gt < 0 || gt - start > 1000) return false;
        var inner = text.Substring(start + 1, gt - start - 1);
        var m = TagName.Match(inner);
        if (!m.Success) return false;                    // "a < b": not a tag

        closing = m.Groups[1].Length > 0;
        name = Canonical(m.Groups[2].Value.ToLowerInvariant());
        attributes = inner[m.Length..];
        selfClosing = inner.TrimEnd().EndsWith('/');
        end = gt + 1;
        return true;
    }

    private static string Canonical(string name) => name switch
    {
        "strong" => "b",
        "em" => "i",
        "ins" => "u",
        "strike" or "del" => "s",
        _ => name
    };

    private static string Decode(string text)
    {
        if (text.IndexOf('&') < 0) return text;
        return Entity.Replace(text, m =>
        {
            var body = m.Groups[1].Value;
            if (body[0] == '#')
            {
                bool hex = body.Length > 1 && (body[1] == 'x' || body[1] == 'X');
                if (int.TryParse(body.AsSpan(hex ? 2 : 1), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                        CultureInfo.InvariantCulture, out var code)
                    && code > 0 && code <= 0x10FFFF && !(code >= 0xD800 && code <= 0xDFFF))
                    return char.ConvertFromUtf32(code);
                return m.Value;
            }
            return body switch
            {
                "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", "nbsp" => " ",
                _ => m.Value
            };
        });
    }

    private static void Add(List<TextRun> runs, string text, Style style)
    {
        if (runs.Count > 0)
        {
            var last = runs[^1];
            if (last.Bold == style.Bold && last.Italic == style.Italic && last.Underline == style.Underline
                && last.Strike == style.Strike && last.Color == style.Color)
            {
                runs[^1] = last with { Text = last.Text + text };
                return;
            }
        }
        runs.Add(new TextRun(text, style.Bold, style.Italic, style.Underline, style.Strike, style.Color));
    }
}
