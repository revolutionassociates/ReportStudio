using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Record filter (requirement 18).</summary>
public class FilterTests
{
    private static Dictionary<string, object?> Row(string dept, decimal amount, string status = "Active") =>
        new(StringComparer.OrdinalIgnoreCase) { ["Dept"] = dept, ["Amount"] = amount, ["Status"] = status };

    private static ReportDefinition Report(string filter)
    {
        var def = new ReportDefinition { Name = "Filtered", Filter = filter };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        def.ReportFooter.Height = 20;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Amount}", Width = 100, Height = 18 });
        def.ReportFooter.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Sum(Amount)}/{Count()}", Width = 100, Height = 18 });
        return def;
    }

    private static ReportResult Run(ReportDefinition def, IDictionary<string, string?>? parameters = null,
        params Dictionary<string, object?>[] rows) =>
        ReportEngine.Run(def, parameters, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static List<string> Texts(ReportResult result, BandKind band) =>
        result.Pages.SelectMany(p => p.Items).Where(i => i.Band == band).Select(i => i.Text).ToList();

    [Fact]
    public void Only_matching_rows_are_printed_and_counted()
    {
        var def = Report("Status == 'Active'");

        var result = Run(def, null, Row("A", 10), Row("A", 20, "Left"), Row("B", 30));

        Assert.Equal(new[] { "10", "30" }, Texts(result, BandKind.Detail));
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public void Totals_count_only_the_kept_rows()
    {
        var def = Report("Amount > 15");

        var result = Run(def, null, Row("A", 10), Row("A", 20), Row("B", 30));

        Assert.Equal(new[] { "50/2" }, Texts(result, BandKind.ReportFooter));
    }

    [Fact]
    public void A_group_with_no_kept_rows_does_not_appear()
    {
        var def = Report("Dept == 'B'");
        def.Groups.Add(new GroupDefinition { Expression = "Dept" });
        def.Groups[0].Header.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Dept}", Width = 100, Height = 18 });

        var result = Run(def, null, Row("A", 10), Row("B", 20), Row("C", 30));

        Assert.Equal(new[] { "B" }, Texts(result, BandKind.GroupHeader));
    }

    [Fact]
    public void The_filter_can_use_a_parameter()
    {
        var def = Report("Amount >= @Min");
        def.Parameters.Add(new ReportParameter { Name = "Min", Type = ParameterType.Number, DefaultValue = "0" });

        var all = Run(def, null, Row("A", 10), Row("A", 20), Row("A", 30));
        var some = Run(def, new Dictionary<string, string?> { ["Min"] = "20" }, Row("A", 10), Row("A", 20), Row("A", 30));

        Assert.Equal(3, all.RowCount);
        Assert.Equal(2, some.RowCount);
    }

    [Fact]
    public void An_empty_filter_keeps_every_row()
    {
        var result = Run(Report(""), null, Row("A", 10), Row("A", 20));
        Assert.Equal(2, result.RowCount);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_filter_that_cannot_be_worked_out_is_a_warning_and_leaves_the_rows_out()
    {
        var result = Run(Report("Nope(1)"), null, Row("A", 10), Row("A", 20));

        Assert.Equal(0, result.RowCount);
        Assert.Single(result.Warnings, w => w.StartsWith("Filter 'Nope(1)'"));
    }

    [Fact]
    public void The_callers_rows_are_not_changed()
    {
        var rows = new List<Dictionary<string, object?>> { Row("A", 10), Row("A", 20) };
        var def = Report("Amount > 15");

        var result = ReportEngine.Run(def, null, new ReportOptions { Culture = CultureInfo.InvariantCulture, Rows = rows });

        Assert.Equal(1, result.RowCount);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void The_filter_survives_saving_and_loading()
    {
        var def = Report("Status == 'Active' && Amount > 0");
        Assert.Equal("Status == 'Active' && Amount > 0", def.Clone().Filter);
    }
}
