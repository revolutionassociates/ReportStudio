using System.Globalization;
using System.Text.RegularExpressions;

namespace ReportStudio.Core.Data.Sources;

/// <summary>Turns the text of a CSV or XML value into a number, date or flag when it plainly is one; otherwise it stays text.</summary>
public static class ValueParser
{
    // No leading zeros ("007" is a code, not 7), no plus sign, at most 15 digits before the point: longer digit strings are ids, card or
    // account numbers, and a decimal would make them print differently.
    private static readonly Regex Number = new(@"^-?(0|[1-9][0-9]{0,14})(\.[0-9]{1,15})?$", RegexOptions.Compiled);
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.Compiled);

    public static object? Infer(string? text)
    {
        if (text == null) return null;
        var t = text.Trim();
        if (t.Length == 0) return null;
        if (Number.IsMatch(t) && decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsoDate.IsMatch(t) && DateTime.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date)) return date;
        return Cut(text);
    }

    /// <summary>The text as it is, cut to the longest value kept.</summary>
    public static string Cut(string text) => text.Length > SourceLimits.MaxValueChars ? text[..SourceLimits.MaxValueChars] : text;

    /// <summary>A field name made usable: trimmed, never empty (Column3), and unique within the row (Name, Name_2).</summary>
    public static string FieldName(string? raw, int position, ISet<string> used)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) name = "Column" + (position + 1);
        if (name.Length > 200) name = name[..200];
        var candidate = name;
        for (int n = 2; !used.Add(candidate); n++) candidate = name + "_" + n;
        return candidate;
    }
}
