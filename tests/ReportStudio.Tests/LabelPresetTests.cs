using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Address label sheets (requirement 37, second part): the presets and what applying one does to a report.</summary>
public class LabelPresetTests
{
    private static ReportDefinition Labels(LabelPreset preset)
    {
        var def = new ReportDefinition { Name = "Labels" };
        def.DataSource.Provider = DbProvider.Host;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{A}", Width = 150, Height = 20 });
        preset.ApplyTo(def);
        return def;
    }

    private static ReportResult Run(ReportDefinition def, int rows) =>
        ReportEngine.Run(def, null,
            Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = i }),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

    // The presets are passed by position, because a record is not something the test runner can carry between runs.
    public static IEnumerable<object[]> Positions() => Enumerable.Range(0, LabelPreset.All.Count).Select(i => new object[] { i });

    [Fact]
    public void There_are_presets_with_different_names()
    {
        Assert.NotEmpty(LabelPreset.All);
        Assert.Equal(LabelPreset.All.Count, LabelPreset.All.Select(p => p.Name).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void A_column_is_as_wide_as_a_label(int position)
    {
        var preset = LabelPreset.All[position];
        var def = Labels(preset);

        Assert.Equal(LabelPreset.ToPixels(preset.LabelWidthMm), def.Page.ColumnWidth, 2);
        Assert.Equal(preset.Columns, def.Page.ColumnCount);
        Assert.Equal(ColumnOrder.Across, def.Page.ColumnOrder);
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void The_details_band_is_one_label_high(int position)
    {
        var preset = LabelPreset.All[position];

        Assert.Equal(LabelPreset.ToPixels(preset.LabelHeightMm), Labels(preset).Detail.Height, 2);
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void The_margins_are_never_negative_and_the_page_is_a_standard_size(int position)
    {
        var preset = LabelPreset.All[position];
        var page = Labels(preset).Page;

        Assert.True(page.MarginLeft >= 0 && page.MarginRight >= 0 && page.MarginTop >= 0 && page.MarginBottom >= 0);
        Assert.Equal(preset.Letter ? 816 : 794, page.Width);
        Assert.Equal(preset.Letter ? 1056 : 1123, page.Height);
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void A_full_sheet_of_labels_is_one_page_and_one_more_label_makes_a_second(int position)
    {
        var preset = LabelPreset.All[position];
        var def = Labels(preset);

        var full = Run(def, preset.PerSheet);
        var over = Run(def, preset.PerSheet + 1);

        Assert.Single(full.Pages);
        Assert.Equal(preset.PerSheet, full.Pages[0].Items.Count);
        Assert.Equal(2, over.Pages.Count);
        Assert.Single(over.Pages[1].Items);
    }

    [Fact]
    public void The_first_label_starts_at_the_sheets_top_left_corner()
    {
        var preset = LabelPreset.All[0];
        var result = Run(Labels(preset), 3);

        var first = result.Pages[0].Items.Single(i => i.Text == "1");
        Assert.Equal(LabelPreset.ToPixels(preset.MarginLeftMm), first.X, 2);
        Assert.Equal(LabelPreset.ToPixels(preset.MarginTopMm), first.Y, 2);
    }

    [Fact]
    public void Labels_go_across_the_sheet_with_the_gap_between_them()
    {
        var preset = LabelPreset.All[0];
        var items = Run(Labels(preset), 4).Pages[0].Items;

        double pitch = LabelPreset.ToPixels(preset.LabelWidthMm + preset.GapMm);
        Assert.Equal(pitch, items.Single(i => i.Text == "2").X - items.Single(i => i.Text == "1").X, 2);
        Assert.Equal(items.Single(i => i.Text == "1").Y, items.Single(i => i.Text == "3").Y, 2);
        Assert.Equal(LabelPreset.ToPixels(preset.LabelHeightMm), items.Single(i => i.Text == "4").Y - items.Single(i => i.Text == "1").Y, 2);
    }

    [Fact]
    public void Bands_without_elements_are_made_zero_high_and_bands_with_elements_are_left()
    {
        var def = new ReportDefinition();
        def.PageHeader.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "Keep me", Width = 50, Height = 10 });
        double headerHeight = def.PageHeader.Height;

        LabelPreset.All[0].ApplyTo(def);

        Assert.Equal(headerHeight, def.PageHeader.Height);
        Assert.Equal(0, def.ReportHeader.Height);
        Assert.Equal(0, def.PageFooter.Height);
        Assert.Equal(0, def.ReportFooter.Height);
    }
}
