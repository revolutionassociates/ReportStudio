using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportStudio.Core.Ui;

/// <summary>
/// A language of the designer's windows (requirement 47). The English text of the window is the key, so the designer needs no list of
/// numbered messages: a language file says "this English text is shown as that text". A text the file does not have stays in English.
/// A language file is plain JSON: <c>{ "code": "hi", "name": "हिन्दी", "texts": { "&amp;File": "&amp;फ़ाइल", ... } }</c>.
/// </summary>
public sealed class UiLanguage
{
    /// <summary>The biggest language file that is read (a bigger one is not a language file).</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>The most texts one language file may hold.</summary>
    public const int MaxTexts = 20000;

    /// <summary>The built-in language: nothing is translated.</summary>
    public static UiLanguage English { get; } = new("en", "English", new Dictionary<string, string>());

    public string Code { get; }
    public string Name { get; }
    public IReadOnlyDictionary<string, string> Texts { get; }

    public UiLanguage(string code, string name, IReadOnlyDictionary<string, string> texts)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "xx" : code.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? Code : name.Trim();
        // Keys are compared exactly (they include the & of a menu shortcut); a blank translation counts as "not translated".
        Texts = texts.Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public override string ToString() => Name;

    /// <summary>The text for an English text: the translation, or the English text itself when there is none.</summary>
    public string Translate(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        return Texts.TryGetValue(english, out var text) ? text : english;
    }

    // ---- files ----

    private sealed class FileShape
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public Dictionary<string, string?>? Texts { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // the file says "code", "name", "texts"
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep the translated text readable in the file
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>True for a language code such as "hi", "pt-BR" or "zh-Hans": two or three letters, then optional parts of letters and digits.</summary>
    public static bool IsValidCode(string? code) =>
        code != null && Regex.IsMatch(code.Trim(), @"^[A-Za-z]{2,3}([-_][A-Za-z0-9]{2,8})*$");

    /// <summary>Reads a language from JSON text. Returns null with <paramref name="problem"/> in plain words when it is not a language file.</summary>
    public static UiLanguage? FromJson(string json, out string? problem)
    {
        problem = null;
        FileShape? shape;
        try { shape = JsonSerializer.Deserialize<FileShape>(json, Json); }
        catch (JsonException ex) { problem = "This is not a language file: " + ex.Message; return null; }

        if (shape == null) { problem = "This language file is empty."; return null; }
        var code = shape.Code?.Trim() ?? "";
        if (!IsValidCode(code))
        {
            problem = "The language file needs a \"code\" such as \"hi\" or \"pt-BR\".";
            return null;
        }
        if (shape.Texts == null || shape.Texts.Count == 0) { problem = "The language file has no \"texts\"."; return null; }
        if (shape.Texts.Count > MaxTexts) { problem = $"The language file has more than {MaxTexts} texts."; return null; }

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in shape.Texts)
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) texts[key] = value;
        return new UiLanguage(code, shape.Name ?? code, texts);
    }

    /// <summary>Reads a language file. Returns null with <paramref name="problem"/> when it cannot be read or is not a language file.</summary>
    public static UiLanguage? Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { problem = "The file does not exist."; return null; }
            if (info.Length > MaxFileBytes) { problem = "The file is too big to be a language file."; return null; }
            return FromJson(File.ReadAllText(path), out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The file could not be read: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Every language file (*.json) of a folder, English first and then by name. A file that is not a language file is left out, and
    /// two files with one code give the first (by file name). A folder that does not exist gives just English.
    /// </summary>
    public static List<UiLanguage> LoadFolder(string folder)
    {
        var result = new List<UiLanguage> { English };
        if (!Directory.Exists(folder)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { English.Code };
        var found = new List<UiLanguage>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var language = Load(file, out _);
            if (language != null && seen.Add(language.Code)) found.Add(language);
        }
        result.AddRange(found.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>The finished JSON of a language.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new FileShape { Code = Code, Name = Name, Texts = Texts.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => (string?)p.Value) }, Json);

    /// <summary>
    /// A file for a translator to fill in: every English text with an empty translation (or the translation this language already has).
    /// </summary>
    public static string TemplateJson(string code, string name, IEnumerable<string> englishTexts, UiLanguage? existing = null)
    {
        var texts = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var text in englishTexts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            texts[text] = existing != null && existing.Texts.TryGetValue(text, out var done) ? done : "";
        }
        return JsonSerializer.Serialize(new FileShape { Code = code, Name = name, Texts = new Dictionary<string, string?>(texts) }, Json);
    }

    // ---- checking a translation ----

    /// <summary>
    /// Problems a translator would want to know about, in plain sentences: a {0}-style place holder that the English has but the translation
    /// lost (or the other way round), and a menu shortcut (&amp;) the English has but the translation lost. Texts that are not in
    /// <paramref name="englishTexts"/> are reported as unused (a misspelt key never shows).
    /// </summary>
    public List<string> Problems(IReadOnlyCollection<string> englishTexts)
    {
        var problems = new List<string>();
        foreach (var (key, value) in Texts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!englishTexts.Contains(key)) { problems.Add($"'{key}' is not a text of the designer, so it is never shown."); continue; }

            var wanted = Placeholders(key);
            var got = Placeholders(value);
            if (!wanted.SetEquals(got)) problems.Add($"'{key}': the place holders {{...}} of the translation differ from the English.");
            if (key.Contains('&') && !value.Contains('&')) problems.Add($"'{key}': the translation has no menu shortcut (&).");
        }
        return problems;
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{[^{}]*\}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
}
