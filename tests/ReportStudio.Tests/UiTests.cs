using ReportStudio.Core.Ui;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Languages, colour themes and settings of the designer (requirement 47a).</summary>
public sealed class UiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rs-ui-" + Guid.NewGuid().ToString("N"));

    public UiTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { /* a temp folder left behind is harmless */ }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Hindi = """
        { "code": "hi", "name": "हिन्दी", "texts": { "&File": "&फ़ाइल", "Save": "सहेजें", "Page {0} of {1}": "पृष्ठ {0} / {1}", "Empty": "  " } }
        """;

    // ---- translating ----

    [Fact]
    public void A_text_is_translated_and_one_without_a_translation_stays_english()
    {
        var language = UiLanguage.FromJson(Hindi, out var problem)!;

        Assert.Null(problem);
        Assert.Equal("hi", language.Code);
        Assert.Equal("हिन्दी", language.Name);
        Assert.Equal("&फ़ाइल", language.Translate("&File"));
        Assert.Equal("Open", language.Translate("Open"));
        Assert.Equal("", language.Translate(""));
    }

    [Fact]
    public void A_blank_translation_counts_as_not_translated()
    {
        var language = UiLanguage.FromJson(Hindi, out _)!;

        Assert.DoesNotContain("Empty", language.Texts.Keys);
        Assert.Equal("Empty", language.Translate("Empty"));
    }

    [Fact]
    public void Texts_are_matched_exactly_including_case_and_the_menu_shortcut()
    {
        var language = UiLanguage.FromJson(Hindi, out _)!;

        Assert.Equal("File", language.Translate("File"));
        Assert.Equal("save", language.Translate("save"));
    }

    [Fact]
    public void English_translates_nothing()
    {
        Assert.Equal("&File", UiLanguage.English.Translate("&File"));
        Assert.Empty(UiLanguage.English.Texts);
    }

    // ---- language files ----

    [Theory]
    [InlineData("not json", "not a language file")]
    [InlineData("null", "empty")]
    [InlineData("""{ "name": "x", "texts": { "a": "b" } }""", "\"code\"")]
    [InlineData("""{ "code": "hindi-language-long", "texts": { "a": "b" } }""", "\"code\"")]
    [InlineData("""{ "code": "hi" }""", "\"texts\"")]
    [InlineData("""{ "code": "hi", "texts": { } }""", "\"texts\"")]
    public void A_file_that_is_not_a_language_file_is_refused_in_plain_words(string json, string expected)
    {
        var language = UiLanguage.FromJson(json, out var problem);

        Assert.Null(language);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void A_language_file_may_have_comments_and_trailing_commas_and_any_case_of_names()
    {
        var language = UiLanguage.FromJson("""
            { // the language
              "Code": "pt-BR", "NAME": "Português",
              "texts": { "Save": "Salvar", },
            }
            """, out var problem)!;

        Assert.Null(problem);
        Assert.Equal("pt-BR", language.Code);
        Assert.Equal("Salvar", language.Translate("Save"));
    }

    [Fact]
    public void A_language_without_a_name_is_called_by_its_code()
    {
        var language = UiLanguage.FromJson("""{ "code": "fr", "texts": { "Save": "Enregistrer" } }""", out _)!;

        Assert.Equal("fr", language.Name);
    }

    [Fact]
    public void A_file_with_too_many_texts_is_refused()
    {
        var texts = string.Join(",", Enumerable.Range(0, UiLanguage.MaxTexts + 1).Select(i => $"\"k{i}\":\"v\""));

        var language = UiLanguage.FromJson("{\"code\":\"hi\",\"texts\":{" + texts + "}}", out var problem);

        Assert.Null(language);
        Assert.Contains("more than", problem);
    }

    [Fact]
    public void A_file_is_loaded_and_a_missing_or_huge_file_is_refused()
    {
        var good = Write("hi.json", Hindi);
        Assert.Equal("hi", UiLanguage.Load(good, out _)!.Code);

        Assert.Null(UiLanguage.Load(Path.Combine(_folder, "nothing.json"), out var missing));
        Assert.Contains("does not exist", missing);

        var huge = Path.Combine(_folder, "huge.json");
        File.WriteAllBytes(huge, new byte[UiLanguage.MaxFileBytes + 1]);
        Assert.Null(UiLanguage.Load(huge, out var tooBig));
        Assert.Contains("too big", tooBig);
    }

    [Fact]
    public void A_folder_gives_english_first_then_the_languages_by_name_and_skips_bad_files()
    {
        Write("hi.json", Hindi);
        Write("de.json", """{ "code": "de", "name": "Deutsch", "texts": { "Save": "Speichern" } }""");
        Write("broken.json", "{ nope");
        Write("again.json", """{ "code": "DE", "name": "Zweite", "texts": { "Save": "X" } }""");   // same code as de.json: the first file by name wins
        Write("notes.txt", "ignored");

        var languages = UiLanguage.LoadFolder(_folder);

        Assert.Equal("en", languages[0].Code);
        Assert.Equal(new[] { "Zweite", "हिन्दी" }.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), languages.Skip(1).Select(l => l.Name));
    }

    [Fact]
    public void A_folder_that_does_not_exist_gives_just_english()
    {
        var languages = UiLanguage.LoadFolder(Path.Combine(_folder, "none"));

        Assert.Equal("en", Assert.Single(languages).Code);
    }

    [Fact]
    public void A_language_written_out_reads_back_the_same()
    {
        var language = UiLanguage.FromJson(Hindi, out _)!;

        var back = UiLanguage.FromJson(language.ToJson(), out var problem)!;

        Assert.Null(problem);
        Assert.Equal(language.Code, back.Code);
        Assert.Equal(language.Name, back.Name);
        Assert.Equal(language.Texts.OrderBy(p => p.Key), back.Texts.OrderBy(p => p.Key));
        Assert.Contains("सहेजें", language.ToJson());   // the translated text is readable in the file, not escaped
    }

    [Fact]
    public void A_template_lists_every_english_text_once_with_an_empty_translation()
    {
        var json = UiLanguage.TemplateJson("hi", "Hindi", new[] { "Save", "&File", "Save", " ", "" });

        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;
        var texts = parsed.GetProperty("texts").EnumerateObject().ToList();

        Assert.Equal("hi", parsed.GetProperty("code").GetString());
        Assert.Equal(new[] { "&File", "Save" }, texts.Select(t => t.Name));
        Assert.All(texts, t => Assert.Equal("", t.Value.GetString()));
    }

    [Fact]
    public void A_template_keeps_what_the_language_has_already_translated()
    {
        var existing = UiLanguage.FromJson(Hindi, out _)!;

        var json = UiLanguage.TemplateJson("hi", "Hindi", new[] { "Save", "Open" }, existing);

        var texts = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("texts");
        Assert.Equal("सहेजें", texts.GetProperty("Save").GetString());
        Assert.Equal("", texts.GetProperty("Open").GetString());
    }

    [Fact]
    public void Problems_of_a_translation_are_told_in_plain_sentences()
    {
        var language = UiLanguage.FromJson("""
            { "code": "hi", "texts": {
                "&File": "फ़ाइल",
                "Page {0} of {1}": "पृष्ठ {0}",
                "Save": "सहेजें",
                "Savee": "typo" } }
            """, out _)!;

        var problems = language.Problems(new[] { "&File", "Page {0} of {1}", "Save", "Open" });

        Assert.Contains(problems, p => p.Contains("'&File'") && p.Contains("shortcut"));
        Assert.Contains(problems, p => p.Contains("'Page {0} of {1}'") && p.Contains("place holders"));
        Assert.Contains(problems, p => p.Contains("'Savee'") && p.Contains("never shown"));
        Assert.DoesNotContain(problems, p => p.Contains("'Save'"));
        Assert.Equal(3, problems.Count);
    }

    [Theory]
    [InlineData("hi", true)]
    [InlineData(" pt-BR ", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("eng", true)]
    [InlineData("h", false)]
    [InlineData("hindi", false)]
    [InlineData("hi-", false)]
    [InlineData("1a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_language_code_is_checked(string? code, bool expected)
    {
        Assert.Equal(expected, UiLanguage.IsValidCode(code));
    }

    // ---- colours ----

    [Fact]
    public void Every_palette_is_valid_and_readable()
    {
        foreach (var palette in UiPalette.All)
            Assert.Empty(palette.Check());
    }

    [Fact]
    public void The_dark_palette_is_dark_and_the_light_one_is_light()
    {
        var dark = UiPalette.Rgb(UiPalette.Dark.Back)!.Value;
        var light = UiPalette.Rgb(UiPalette.Light.Back)!.Value;

        Assert.True(dark.R + dark.G + dark.B < 3 * 80);
        Assert.True(light.R + light.G + light.B > 3 * 200);
    }

    [Fact]
    public void Palettes_are_found_by_name_and_an_unknown_name_gives_the_light_one()
    {
        Assert.Same(UiPalette.Dark, UiPalette.ByName(" dark "));
        Assert.Same(UiPalette.Light, UiPalette.ByName("Light"));
        Assert.Same(UiPalette.Light, UiPalette.ByName("Purple"));
        Assert.Same(UiPalette.Light, UiPalette.ByName(null));
    }

    [Theory]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#FF8000", 255, 128, 0)]
    [InlineData("#0a0B0c", 10, 11, 12)]
    public void A_colour_is_read_as_red_green_blue(string hex, int r, int g, int b)
    {
        var rgb = UiPalette.Rgb(hex)!.Value;

        Assert.Equal(r, rgb.R);
        Assert.Equal(g, rgb.G);
        Assert.Equal(b, rgb.B);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000")]
    [InlineData("#12")]
    [InlineData("#GG0000")]
    [InlineData("#-10000")]
    [InlineData("red")]
    public void Text_that_is_not_a_colour_is_not_read(string? hex)
    {
        Assert.Null(UiPalette.Rgb(hex));
    }

    [Fact]
    public void Contrast_is_21_for_black_on_white_and_1_for_the_same_colour()
    {
        Assert.Equal(21, UiPalette.ContrastRatio("#000000", "#FFFFFF")!.Value, 1);
        Assert.Equal(1, UiPalette.ContrastRatio("#336699", "#336699")!.Value, 3);
        Assert.Null(UiPalette.ContrastRatio("blue", "#FFFFFF"));
    }

    [Fact]
    public void A_palette_with_a_bad_colour_or_unreadable_text_is_reported()
    {
        var bad = UiPalette.Light with { Fore = "dark" };
        var faint = UiPalette.Light with { Fore = "#EEEEEE" };

        Assert.Contains(bad.Check(), p => p.Contains("Fore") && p.Contains("#RRGGBB"));
        Assert.Contains(faint.Check(), p => p.Contains("hard to read"));
    }

    // ---- settings ----

    [Fact]
    public void Settings_that_were_never_saved_give_the_defaults()
    {
        var settings = DesignerSettings.Load(Path.Combine(_folder, "none.json"));

        Assert.Equal("System", settings.Theme);
        Assert.Equal("en", settings.Language);
    }

    [Fact]
    public void Settings_are_saved_and_loaded_and_the_folder_is_made()
    {
        var path = Path.Combine(_folder, "deep", "er", "settings.json");

        Assert.True(new DesignerSettings { Theme = "Dark", Language = "hi", Ribbon = true }.Save(path));
        var back = DesignerSettings.Load(path);

        Assert.Equal("Dark", back.Theme);
        Assert.Equal("hi", back.Language);
        Assert.True(back.Ribbon);
        Assert.False(DesignerSettings.Load(Path.Combine(_folder, "none.json")).Ribbon);
    }

    [Fact]
    public void A_damaged_or_oversized_settings_file_gives_the_defaults()
    {
        var damaged = Write("damaged.json", "{ nope");
        var huge = Path.Combine(_folder, "huge.json");
        File.WriteAllBytes(huge, new byte[DesignerSettings.MaxFileBytes + 1]);

        Assert.Equal("System", DesignerSettings.Load(damaged).Theme);
        Assert.Equal("System", DesignerSettings.Load(huge).Theme);
    }

    [Fact]
    public void An_unknown_theme_or_blank_language_in_the_file_is_put_right()
    {
        var path = Write("odd.json", """{ "theme": "purple", "language": "  " }""");

        var settings = DesignerSettings.Load(path);

        Assert.Equal("System", settings.Theme);
        Assert.Equal("en", settings.Language);
    }

    [Theory]
    [InlineData("dark", "Dark")]
    [InlineData(" LIGHT ", "Light")]
    [InlineData("system", "System")]
    [InlineData(null, "System")]
    [InlineData("blue", "System")]
    public void A_theme_name_is_put_right(string? name, string expected)
    {
        Assert.Equal(expected, DesignerSettings.NormalizeTheme(name));
    }

    [Fact]
    public void The_palette_follows_the_chosen_theme_and_for_system_follows_windows()
    {
        Assert.Same(UiPalette.Dark, new DesignerSettings { Theme = "Dark" }.Palette(systemIsDark: false));
        Assert.Same(UiPalette.Light, new DesignerSettings { Theme = "Light" }.Palette(systemIsDark: true));
        Assert.Same(UiPalette.Dark, new DesignerSettings { Theme = "System" }.Palette(systemIsDark: true));
        Assert.Same(UiPalette.Light, new DesignerSettings { Theme = "System" }.Palette(systemIsDark: false));
        Assert.Same(UiPalette.Light, new DesignerSettings { Theme = "nonsense" }.Palette(systemIsDark: false));
    }

    [Fact]
    public void Settings_that_cannot_be_written_report_false_instead_of_throwing()
    {
        var blocker = Write("blocker", "a file where a folder is needed");

        Assert.False(new DesignerSettings().Save(Path.Combine(blocker, "settings.json")));
    }
}
