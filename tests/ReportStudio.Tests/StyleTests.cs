using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Styles and themes (requirement 46a).</summary>
public class StyleTests
{
    private static ReportItem Text(string text, string style = "") => new()
    {
        Kind = ItemKind.Text, X = 0, Y = 0, Width = 200, Height = 18, Text = text, Style = style, FontSize = 9, ForeColor = "#000000"
    };

    private static ReportDefinition Styled()
    {
        var def = new ReportDefinition { Name = "Styled" };
        def.DataSource.Provider = DbProvider.Host;
        def.DataSource.Fields = FieldDefinition.ParseLines(new[] { "Name", "Amount:Number" });
        def.Styles.Add(new StyleDefinition { Name = "Body", FontSize = 11, Bold = true, ForeColor = "#112233" });
        def.Detail.Height = 20;
        def.Detail.Items.Add(Text("{Name}", "Body"));
        return def;
    }

    private static Dictionary<string, object?> Row(string name, decimal amount) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Name"] = name, ["Amount"] = amount };

    private static ReportResult Run(ReportDefinition def, params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static RenderedItem DetailItem(ReportResult result) =>
        result.Pages.SelectMany(p => p.Items).First(i => i.Band == BandKind.Detail && i.Kind == ItemKind.Text);

    // ---- finding and inheriting ----

    [Fact]
    public void A_style_is_found_by_name_without_regard_to_case_or_spaces()
    {
        var def = Styled();

        Assert.Same(def.Styles[0], StyleResolver.Find(def, " body "));
        Assert.Null(StyleResolver.Find(def, "Nothing"));
        Assert.Null(StyleResolver.Find(def, ""));
        Assert.Null(StyleResolver.Find(def, null));
    }

    [Fact]
    public void A_style_based_on_another_starts_with_its_values_and_overrides_what_it_sets()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "Base", FontSize = 10, Bold = true, ForeColor = "#111111", HAlign = HAlign.Left });
        def.Styles.Add(new StyleDefinition { Name = "Big", Parent = "Base", FontSize = 20, ForeColor = "#222222" });

        var effective = StyleResolver.Effective(def, "Big")!;

        Assert.Equal(20, effective.FontSize);
        Assert.Equal("#222222", effective.ForeColor);
        Assert.True(effective.Bold);
        Assert.Equal(HAlign.Left, effective.HAlign);
        Assert.Null(effective.Italic);
    }

    [Fact]
    public void The_style_the_other_is_based_on_is_not_changed_by_working_out_the_values()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "Base", FontSize = 10 });
        def.Styles.Add(new StyleDefinition { Name = "Big", Parent = "Base", Bold = true });

        StyleResolver.Effective(def, "Big");

        Assert.Equal(10, def.Styles[0].FontSize);
        Assert.Null(def.Styles[0].Bold);
        Assert.Null(def.Styles[1].FontSize);
    }

    [Fact]
    public void A_style_that_does_not_exist_has_no_effective_values()
    {
        Assert.Null(StyleResolver.Effective(new ReportDefinition(), "Body"));
    }

    [Fact]
    public void A_missing_parent_just_ends_the_chain()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "Lost", Parent = "Gone", Bold = true });

        Assert.True(StyleResolver.Effective(def, "Lost")!.Bold);
    }

    [Fact]
    public void Styles_based_on_each_other_in_a_loop_still_give_values()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "A", Parent = "B", FontSize = 12 });
        def.Styles.Add(new StyleDefinition { Name = "B", Parent = "A", Bold = true });
        def.Styles.Add(new StyleDefinition { Name = "Self", Parent = "Self", Italic = true });

        var a = StyleResolver.Effective(def, "A")!;

        Assert.Equal(12, a.FontSize);
        Assert.True(a.Bold);
        Assert.True(StyleResolver.Effective(def, "Self")!.Italic);
    }

    [Fact]
    public void A_chain_is_followed_for_eight_styles_and_no_further()
    {
        ReportDefinition Chain(int length)
        {
            var def = new ReportDefinition();
            def.Styles.Add(new StyleDefinition { Name = "s0", FontSize = 14 });
            for (int i = 1; i < length; i++) def.Styles.Add(new StyleDefinition { Name = "s" + i, Parent = "s" + (i - 1) });
            return def;
        }

        Assert.Equal(14, StyleResolver.Effective(Chain(8), "s7")!.FontSize);
        Assert.Null(StyleResolver.Effective(Chain(12), "s11")!.FontSize);
    }

    // ---- applying ----

    [Fact]
    public void A_style_puts_the_values_it_sets_on_the_element_and_leaves_the_rest()
    {
        var item = Text("x");
        item.Italic = true;
        item.BackColor = "#ABCDEF";
        var style = new StyleDefinition
        {
            FontFamily = "Georgia", FontSize = 14, Bold = true, Underline = true, HAlign = HAlign.Right, VAlign = VAlign.Bottom,
            ForeColor = "#FF0000", BorderColor = "#00FF00", BorderWidth = 2, CornerRadius = 4
        };

        StyleResolver.Apply(style, item);

        Assert.Equal("Georgia", item.FontFamily);
        Assert.Equal(14, item.FontSize);
        Assert.True(item.Bold);
        Assert.True(item.Underline);
        Assert.Equal(HAlign.Right, item.HAlign);
        Assert.Equal(VAlign.Bottom, item.VAlign);
        Assert.Equal("#FF0000", item.ForeColor);
        Assert.Equal("#00FF00", item.BorderColor);
        Assert.Equal(2, item.BorderWidth);
        Assert.Equal(4, item.CornerRadius);
        Assert.True(item.Italic);                    // the style says nothing about it
        Assert.Equal("#ABCDEF", item.BackColor);     // nor about the fill
    }

    [Fact]
    public void An_empty_colour_in_a_style_means_no_colour_and_replaces_the_elements()
    {
        var item = Text("x");
        item.BackColor = "#ABCDEF";

        StyleResolver.Apply(new StyleDefinition { BackColor = "" }, item);

        Assert.Equal("", item.BackColor);
    }

    [Fact]
    public void Odd_values_in_a_style_are_kept_within_bounds()
    {
        var item = Text("x");
        item.FontFamily = "Arial";

        StyleResolver.Apply(new StyleDefinition { FontFamily = "  ", FontSize = 5000, BorderWidth = -3, CornerRadius = -1 }, item);
        Assert.Equal("Arial", item.FontFamily);
        Assert.Equal(600, item.FontSize);
        Assert.Equal(0, item.BorderWidth);
        Assert.Equal(0, item.CornerRadius);

        StyleResolver.Apply(new StyleDefinition { FontSize = 1 }, item);
        Assert.Equal(6, item.FontSize);

        StyleResolver.Apply(new StyleDefinition { FontSize = double.NaN, BorderWidth = double.PositiveInfinity }, item);
        Assert.Equal(6, item.FontSize);
        Assert.Equal(0, item.BorderWidth);
    }

    // ---- resolving a report ----

    [Fact]
    public void A_report_that_uses_no_style_is_laid_out_as_it_is()
    {
        var plain = new ReportDefinition();
        plain.Detail.Items.Add(Text("a"));

        var unused = new ReportDefinition();
        unused.Styles.Add(new StyleDefinition { Name = "Body", Bold = true });
        unused.Detail.Items.Add(Text("a"));

        Assert.Same(plain, StyleResolver.Resolve(plain));
        Assert.Same(unused, StyleResolver.Resolve(unused));
    }

    [Fact]
    public void A_report_with_styled_elements_gets_a_copy_and_the_original_is_not_changed()
    {
        var def = Styled();

        var resolved = StyleResolver.Resolve(def);

        Assert.NotSame(def, resolved);
        var copy = resolved.Detail.Items.Single();
        Assert.True(copy.Bold);
        Assert.Equal(11, copy.FontSize);
        Assert.Equal("#112233", copy.ForeColor);
        var original = def.Detail.Items.Single();
        Assert.False(original.Bold);
        Assert.Equal(9, original.FontSize);
        Assert.Equal("#000000", original.ForeColor);
    }

    [Fact]
    public void Elements_in_every_band_take_their_style()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "S", Bold = true });
        var group = new GroupDefinition { Expression = "Name" };
        group.Header.Items.Add(Text("gh", "S"));
        group.Footer.Items.Add(Text("gf", "S"));
        def.Groups.Add(group);
        def.ReportHeader.Items.Add(Text("rh", "S"));
        def.PageHeader.Items.Add(Text("ph", "S"));
        def.Detail.Items.Add(Text("d", "S"));
        def.ReportFooter.Items.Add(Text("rf", "S"));
        def.PageFooter.Items.Add(Text("pf", "S"));

        var items = StyleResolver.Resolve(def).AllBands().SelectMany(b => b.Items).ToList();

        Assert.Equal(7, items.Count);
        Assert.All(items, i => Assert.True(i.Bold));
    }

    [Fact]
    public void A_style_based_on_another_is_applied_with_both()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "Base", Bold = true, FontSize = 10 });
        def.Styles.Add(new StyleDefinition { Name = "Big", Parent = "Base", FontSize = 20 });
        def.Detail.Items.Add(Text("x", "Big"));

        var item = StyleResolver.Resolve(def).Detail.Items.Single();

        Assert.True(item.Bold);
        Assert.Equal(20, item.FontSize);
    }

    [Fact]
    public void An_element_that_names_a_missing_style_keeps_its_own_look_and_is_reported_once_per_name()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "Other", Bold = true });
        def.Detail.Items.Add(Text("a", "Missing"));
        def.Detail.Items.Add(Text("b", "missing"));
        def.PageFooter.Items.Add(Text("c", "Other"));

        var warnings = StyleResolver.Warnings(def);
        var resolved = StyleResolver.Resolve(def);

        var warning = Assert.Single(warnings);
        Assert.Contains("'Missing'", warning);
        Assert.False(resolved.Detail.Items[0].Bold);
        Assert.True(resolved.PageFooter.Items[0].Bold);
    }

    // ---- checking ----

    [Fact]
    public void A_fine_set_of_styles_has_no_problems()
    {
        var def = Styled();
        def.Styles.Add(new StyleDefinition { Name = "Big", Parent = "Body", FontSize = 20, BackColor = "", BorderColor = "#00000080" });

        Assert.Empty(StyleResolver.Check(def));
    }

    [Fact]
    public void Style_problems_are_told_in_plain_sentences()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "" });
        def.Styles.Add(new StyleDefinition { Name = "Dup" });
        def.Styles.Add(new StyleDefinition { Name = "dup" });
        def.Styles.Add(new StyleDefinition { Name = "Orphan", Parent = "Nope" });
        def.Styles.Add(new StyleDefinition { Name = "A", Parent = "B" });
        def.Styles.Add(new StyleDefinition { Name = "B", Parent = "A" });
        def.Styles.Add(new StyleDefinition { Name = "Bad", ForeColor = "#12", BackColor = "12x4", FontSize = 3, BorderWidth = -1, CornerRadius = -2 });
        def.Detail.Items.Add(Text("x", "Unknown"));

        var problems = StyleResolver.Check(def);

        Assert.Contains("A style has no name.", problems);
        Assert.Contains("Two styles are called 'dup'.", problems);
        Assert.Contains(problems, p => p.Contains("'Orphan'") && p.Contains("'Nope'"));
        Assert.Contains(problems, p => p.Contains("'A'") && p.Contains("based on itself"));
        Assert.Contains(problems, p => p.Contains("'B'") && p.Contains("based on itself"));
        Assert.Contains(problems, p => p.Contains("text colour"));
        Assert.Contains(problems, p => p.Contains("fill colour"));
        Assert.Contains(problems, p => p.Contains("font size"));
        Assert.Contains(problems, p => p.Contains("border width"));
        Assert.Contains(problems, p => p.Contains("corner radius"));
        Assert.Contains(problems, p => p.Contains("'Unknown'") && p.Contains("elements use it"));
    }

    // ---- saving ----

    [Fact]
    public void Styles_and_the_style_of_an_element_survive_saving_and_loading()
    {
        var def = Styled();
        def.Styles.Add(new StyleDefinition { Name = "Big", Parent = "Body", FontSize = 12.5, HAlign = HAlign.Center, BackColor = "" });

        var copy = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal(2, copy.Styles.Count);
        var big = copy.Styles[1];
        Assert.Equal("Body", big.Parent);
        Assert.Equal(12.5, big.FontSize);
        Assert.Equal(HAlign.Center, big.HAlign);
        Assert.Equal("", big.BackColor);
        Assert.Null(big.Underline);
        Assert.Null(big.ForeColor);
        Assert.Equal("Body", copy.Detail.Items.Single().Style);
        Assert.Equal(def.ToJson(), copy.ToJson());
    }

    [Fact]
    public void A_report_saved_before_styles_existed_loads_without_any()
    {
        var copy = ReportDefinition.FromJson(new ReportDefinition().ToJson());

        Assert.Empty(copy.Styles);
        Assert.True(string.IsNullOrEmpty(new ReportItem().Style));
    }

    [Fact]
    public void Copying_the_format_of_an_element_copies_its_style_too()
    {
        var from = Text("a", "Body");
        var to = Text("b");

        ItemArrange.CopyAppearance(from, to);

        Assert.Equal("Body", to.Style);
    }

    // ---- in the engine ----

    [Fact]
    public void The_engine_prints_a_styled_element_with_the_values_of_its_style()
    {
        var result = Run(Styled(), Row("Ann", 10));

        var item = DetailItem(result);
        Assert.Equal("Ann", item.Text);
        Assert.True(item.Bold);
        Assert.Equal(11, item.FontSize);
        Assert.Equal("#112233", item.ForeColor);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Changing_the_style_changes_every_element_that_uses_it()
    {
        var def = Styled();
        def.Detail.Items.Add(Text("{Amount}", "Body"));
        def.Styles[0].ForeColor = "#AA0000";

        var result = Run(def, Row("Ann", 10));

        var colours = result.Pages.SelectMany(p => p.Items).Where(i => i.Band == BandKind.Detail && i.Kind == ItemKind.Text)
            .Select(i => i.ForeColor).ToList();
        Assert.Equal(new[] { "#AA0000", "#AA0000" }, colours);
    }

    [Fact]
    public void The_engine_does_not_change_the_report_it_is_given()
    {
        var def = Styled();

        Run(def, Row("Ann", 10));

        var item = def.Detail.Items.Single();
        Assert.False(item.Bold);
        Assert.Equal(9, item.FontSize);
    }

    [Fact]
    public void A_conditional_format_still_wins_over_the_style()
    {
        var def = Styled();
        def.Detail.Items[0].Rules.Add(new FormatRule { Condition = "Amount > 5", ForeColor = "#FF0000" });

        var result = Run(def, Row("Big", 10), Row("Small", 1));

        var colours = result.Pages.SelectMany(p => p.Items).Where(i => i.Band == BandKind.Detail && i.Kind == ItemKind.Text)
            .Select(i => i.ForeColor).ToList();
        Assert.Equal(new[] { "#FF0000", "#112233" }, colours);
    }

    [Fact]
    public void A_missing_style_is_a_warning_in_the_result_and_the_element_keeps_its_look()
    {
        var def = Styled();
        def.Detail.Items[0].Style = "Gone";

        var result = Run(def, Row("Ann", 10));

        Assert.Contains(result.Warnings, w => w.Contains("'Gone'"));
        var item = DetailItem(result);
        Assert.False(item.Bold);
        Assert.Equal(9, item.FontSize);
    }

    // ---- themes ----

    [Fact]
    public void Every_theme_defines_every_standard_style_and_they_are_fine()
    {
        Assert.NotEmpty(StyleThemes.All);
        foreach (var theme in StyleThemes.All)
        {
            var def = new ReportDefinition();
            int count = StyleThemes.Apply(def, theme);

            Assert.Equal(StyleThemes.StandardNames.Count, count);
            Assert.Equal(StyleThemes.StandardNames, def.Styles.Select(s => s.Name));
            Assert.Empty(StyleResolver.Check(def));
            Assert.False(string.IsNullOrWhiteSpace(theme.Description));
        }
    }

    [Fact]
    public void Themes_are_found_by_name_without_regard_to_case()
    {
        Assert.Equal("Modern", StyleThemes.Find(" modern ")!.Name);
        Assert.Null(StyleThemes.Find("Nothing"));
        Assert.Null(StyleThemes.Find(null));
        Assert.Equal(StyleThemes.All.Count, StyleThemes.All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Applying_a_theme_replaces_its_styles_and_keeps_the_others()
    {
        var def = new ReportDefinition();
        def.Styles.Add(new StyleDefinition { Name = "Mine", Bold = true });
        def.Styles.Add(new StyleDefinition { Name = "column TITLE", ForeColor = "#123456" });

        StyleThemes.Apply(def, StyleThemes.Find("Corporate")!);

        Assert.Equal(1 + StyleThemes.StandardNames.Count, def.Styles.Count);
        Assert.Contains(def.Styles, s => s.Name == "Mine");
        var columnTitle = Assert.Single(def.Styles, s => string.Equals(s.Name, StyleThemes.ColumnTitle, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("#FFFFFF", columnTitle.ForeColor);
    }

    [Fact]
    public void Another_theme_gives_the_same_report_another_look_without_touching_its_elements()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.Host;
        def.DataSource.Fields = FieldDefinition.ParseLines(new[] { "Name" });
        def.Detail.Height = 20;
        def.Detail.Items.Add(Text("{Name}", StyleThemes.ColumnTitle));

        StyleThemes.Apply(def, StyleThemes.Find("Corporate")!);
        var corporate = DetailItem(Run(def, Row("Ann", 1)));
        StyleThemes.Apply(def, StyleThemes.Find("Classic")!);
        var classic = DetailItem(Run(def, Row("Ann", 1)));

        Assert.Equal("#1F3A5F", corporate.BackColor);
        Assert.Equal("", classic.BackColor);
        Assert.Equal("Name", def.Detail.Items.Single().Text.Trim('{', '}'));
    }

    [Fact]
    public void Each_use_of_a_theme_gives_independent_styles()
    {
        var theme = StyleThemes.Find("Simple")!;
        var first = theme.Create();
        first[0].Bold = false;
        first[0].FontSize = 99;

        var second = theme.Create();

        Assert.Equal(18, second[0].FontSize);
        Assert.True(second[0].Bold);
    }

    [Fact]
    public void Applying_a_theme_needs_a_report_and_a_theme()
    {
        Assert.Throws<ArgumentNullException>(() => StyleThemes.Apply(null!, StyleThemes.All[0]));
        Assert.Throws<ArgumentNullException>(() => StyleThemes.Apply(new ReportDefinition(), null!));
    }
}
