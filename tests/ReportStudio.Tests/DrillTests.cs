using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Drill-down (open/close a group) and drill-through (open another report), requirement 30.</summary>
public class DrillTests
{
    private static Dictionary<string, object?> Row(string country, string customer, decimal amount) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Country"] = country, ["Customer"] = customer, ["Amount"] = amount };

    private static List<Dictionary<string, object?>> Rows() => new()
    {
        Row("India", "A", 10), Row("India", "B", 20), Row("Germany", "C", 5), Row("Germany", "D", 7), Row("Germany", "E", 8)
    };

    private static ReportDefinition Report(bool collapsible = true, bool startCollapsed = false)
    {
        var def = new ReportDefinition { Name = "Drill" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 20;
        def.ReportFooter.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "All {Sum(Amount)}", Width = 100, Height = 18 });
        def.PageFooter.Height = 0;
        var group = new GroupDefinition { Expression = "Country", Collapsible = collapsible, StartCollapsed = startCollapsed };
        group.Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{DrillMark} {Country}", Width = 100, Height = 20 });
        group.Footer.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "Sub {Sum(Amount)}", Width = 100, Height = 20, X = 200 });
        def.Groups.Add(group);
        def.Detail.Height = 20;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Customer}", Width = 100, Height = 18 });
        return def;
    }

    private static List<RenderedItem> Run(ReportDefinition def, ISet<string>? toggled = null) =>
        ReportEngine.Run(def, null, Rows(), new ReportOptions { Culture = CultureInfo.InvariantCulture, ToggledGroups = toggled })
            .Pages.SelectMany(p => p.Items).ToList();

    private static List<string> Texts(IEnumerable<RenderedItem> items, BandKind band) =>
        items.Where(i => i.Band == band).Select(i => i.Text).ToList();

    [Fact]
    public void An_open_group_prints_its_rows_and_shows_a_minus()
    {
        var items = Run(Report());

        Assert.Equal(5, Texts(items, BandKind.Detail).Count);
        Assert.Equal(new[] { "- Germany", "- India" }, Texts(items, BandKind.GroupHeader));   // groups come out in key order
    }

    [Fact]
    public void Header_items_of_a_collapsible_group_are_clickable_and_each_instance_has_its_own_key()
    {
        var headers = Run(Report()).Where(i => i.Band == BandKind.GroupHeader).ToList();

        Assert.All(headers, h => Assert.NotEmpty(h.DrillKey));
        Assert.Equal(2, headers.Select(h => h.DrillKey).Distinct().Count());
        Assert.All(Run(Report()).Where(i => i.Band != BandKind.GroupHeader), i => Assert.Equal("", i.DrillKey));
    }

    [Fact]
    public void Toggling_a_key_closes_that_group_only_and_keeps_header_footer_and_totals()
    {
        var def = Report();
        var germany = Run(def).First(i => i.Band == BandKind.GroupHeader).DrillKey;

        var items = Run(def, new HashSet<string> { germany });

        Assert.Equal(new[] { "A", "B" }, Texts(items, BandKind.Detail));   // only India's rows remain
        Assert.Equal(new[] { "+ Germany", "- India" }, Texts(items, BandKind.GroupHeader));
        Assert.Equal(new[] { "Sub 20", "Sub 30" }, Texts(items, BandKind.GroupFooter));   // the closed group still totals its rows
        Assert.Equal(new[] { "All 50" }, Texts(items, BandKind.ReportFooter));
    }

    [Fact]
    public void A_group_that_starts_closed_is_opened_by_toggling()
    {
        var def = Report(startCollapsed: true);

        var closed = Run(def);
        Assert.Empty(Texts(closed, BandKind.Detail));
        Assert.Equal(new[] { "+ Germany", "+ India" }, Texts(closed, BandKind.GroupHeader));

        var key = closed.First(i => i.Band == BandKind.GroupHeader).DrillKey;
        var open = Run(def, new HashSet<string> { key });
        Assert.Equal(new[] { "C", "D", "E" }, Texts(open, BandKind.Detail));
        Assert.Equal(new[] { "- Germany", "+ India" }, Texts(open, BandKind.GroupHeader));
    }

    [Fact]
    public void A_group_that_is_not_collapsible_ignores_toggles_and_has_no_mark()
    {
        var def = Report(collapsible: false);
        var items = Run(def, new HashSet<string> { "/0:India" });

        Assert.Equal(5, Texts(items, BandKind.Detail).Count);
        Assert.Equal(new[] { " Germany", " India" }, Texts(items, BandKind.GroupHeader));
        Assert.All(items, i => Assert.Equal("", i.DrillKey));
    }

    [Fact]
    public void Inner_groups_have_keys_that_include_the_outer_group()
    {
        var def = Report();
        var inner = new GroupDefinition { Expression = "Customer", Collapsible = true };
        inner.Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{DrillMark} {Customer}", Width = 100, Height = 20 });
        def.Groups.Add(inner);

        var keys = Run(def).Where(i => i.Band == BandKind.GroupHeader && i.Text.EndsWith("A")).Select(i => i.DrillKey).ToList();

        Assert.Single(keys);
        Assert.Contains("India", keys[0]);   // customer A's key carries its country
        Assert.Contains("A", keys[0]);
    }

    // ---- drill-through ----

    [Fact]
    public void A_drill_through_item_carries_the_report_and_its_parameter_values_for_each_row()
    {
        var def = Report();
        def.Detail.Items[0].DrillReport = "detail-{Country}.rsr";
        def.Detail.Items[0].DrillParameters = "Customer={Customer}; Max={Amount * 2}";

        var item = Run(def).First(i => i.Band == BandKind.Detail);

        Assert.Equal("detail-Germany.rsr", item.DrillReport);   // the first row printed is Germany's C
        Assert.Equal("C", item.DrillParameters["Customer"]);
        Assert.Equal("10", item.DrillParameters["Max"]);
    }

    [Fact]
    public void Items_without_drill_settings_are_unchanged()
    {
        var item = Run(Report()).First(i => i.Band == BandKind.Detail);

        Assert.Equal("", item.DrillReport);
        Assert.Empty(item.DrillParameters);
    }

    [Fact]
    public void Drill_settings_survive_saving()
    {
        var def = Report(startCollapsed: true);
        def.Detail.Items[0].DrillReport = "x.rsr";
        def.Detail.Items[0].DrillParameters = "A={B}";

        var loaded = ReportDefinition.FromJson(def.ToJson());

        Assert.True(loaded.Groups[0].Collapsible);
        Assert.True(loaded.Groups[0].StartCollapsed);
        Assert.Equal("x.rsr", loaded.Detail.Items[0].DrillReport);
        Assert.Equal("A={B}", loaded.Detail.Items[0].DrillParameters);
    }

    [Fact]
    public void Only_rsr_files_inside_the_reports_folder_can_be_opened()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-drill-test");

        var ok = DrillTarget.ResolveFile(dir, "detail.rsr");
        var sub = DrillTarget.ResolveFile(dir, "sub/detail.RSR");

        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "detail.rsr"), ok);
        Assert.NotNull(sub);
        Assert.Null(DrillTarget.ResolveFile(dir, "../outside.rsr"));
        Assert.Null(DrillTarget.ResolveFile(dir, "sub/../../outside.rsr"));
        Assert.Null(DrillTarget.ResolveFile(dir, Path.Combine(Path.GetTempPath(), "other.rsr")));   // absolute
        Assert.Null(DrillTarget.ResolveFile(dir, "run.exe"));
        Assert.Null(DrillTarget.ResolveFile(dir, "notes.txt"));
        Assert.Null(DrillTarget.ResolveFile(dir, ""));
        Assert.Null(DrillTarget.ResolveFile(dir, null));
        Assert.Null(DrillTarget.ResolveFile(null, "detail.rsr"));
        Assert.Null(DrillTarget.ResolveFile(dir, "a\nb.rsr"));
    }
}
