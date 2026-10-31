namespace ReportStudio.Core.Text;

/// <summary>The Indian scripts that need glyph shaping (conjuncts, reordered vowel signs) to read correctly.</summary>
public enum IndicScript
{
    None,
    Devanagari,
    Bengali,
    Gurmukhi,
    Gujarati,
    Odia,
    Tamil,
    Telugu,
    Kannada,
    Malayalam
}

/// <summary>
/// Finds which Indic script a text uses and which fonts can show it. The PDF library draws text glyph by glyph
/// without shaping, so Hindi, Tamil and the other scripts would come out with broken conjuncts; the PDF exporter
/// uses this to send such text through the shaping text engine instead.
/// </summary>
public static class ComplexScript
{
    /// <summary>The first Indic script found in the text, or None.</summary>
    public static IndicScript Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return IndicScript.None;
        foreach (var ch in text)
        {
            var script = Of(ch);
            if (script != IndicScript.None) return script;
        }
        return IndicScript.None;
    }

    public static bool ContainsIndic(string? text) => Detect(text) != IndicScript.None;

    public static IndicScript Of(char ch) => ch switch
    {
        >= 'ऀ' and <= 'ॿ' => IndicScript.Devanagari,
        >= '꣠' and <= 'ꣿ' => IndicScript.Devanagari,   // Devanagari Extended
        >= 'ঀ' and <= '৿' => IndicScript.Bengali,
        >= '਀' and <= '੿' => IndicScript.Gurmukhi,
        >= '઀' and <= '૿' => IndicScript.Gujarati,
        >= '଀' and <= '୿' => IndicScript.Odia,
        >= '஀' and <= '௿' => IndicScript.Tamil,
        >= 'ఀ' and <= '౿' => IndicScript.Telugu,
        >= 'ಀ' and <= '೿' => IndicScript.Kannada,
        >= 'ഀ' and <= 'ൿ' => IndicScript.Malayalam,
        _ => IndicScript.None
    };

    /// <summary>
    /// Font families to try, best first: the Noto font for the script (put its .ttf in the Fonts folder to ship it
    /// with the PDF), then the Windows fonts that cover it.
    /// </summary>
    public static IReadOnlyList<string> PreferredFamilies(IndicScript script)
    {
        string noto = script switch
        {
            IndicScript.Devanagari => "Noto Sans Devanagari",
            IndicScript.Bengali => "Noto Sans Bengali",
            IndicScript.Gurmukhi => "Noto Sans Gurmukhi",
            IndicScript.Gujarati => "Noto Sans Gujarati",
            IndicScript.Odia => "Noto Sans Oriya",
            IndicScript.Tamil => "Noto Sans Tamil",
            IndicScript.Telugu => "Noto Sans Telugu",
            IndicScript.Kannada => "Noto Sans Kannada",
            IndicScript.Malayalam => "Noto Sans Malayalam",
            _ => ""
        };
        if (noto.Length == 0) return Array.Empty<string>();
        return new[] { noto, "Nirmala UI", "Mangal", "Latha", "Vijaya", "Raavi", "Shruti", "Kartika", "Tunga", "Vrinda", "Gautami", "Kalinga" };
    }
}
