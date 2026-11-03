using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The designer shows real values instead of {Field} placeholders (requirement 26).</summary>
public class DesignPreviewTests
{
    private static ReportItem Text(string text, string format = "") =>
        new() { Kind = ItemKind.Text, Text = text, Format = format };

    private static List<Dictionary<string, object?>> Rows() => new()
    {
        new() { ["Name"] = "Ravi", ["Amount"] = 100m },
        new() { ["Name"] = "Meena", ["Amount"] = 250.5m }
    };

    private static DesignPreview Preview() => DesignPreview.From(new ReportDefinition(), Rows());

    [Fact]
    public void A_field_shows_the_value_of_the_current_row()
    {
        var p = Preview();
        Assert.Equal("Hello Ravi", p.TextFor(Text("Hello {Name}")));
    }

    [Fact]
    public void Next_moves_to_the_following_row_and_wraps_round()
    {
        var p = Preview();
        p.Next();
        Assert.Equal("Meena", p.TextFor(Text("{Name}")));
        p.Next();
        Assert.Equal(0, p.CurrentIndex);
        Assert.Equal("Ravi", p.TextFor(Text("{Name}")));
    }

    [Fact]
    public void Totals_use_all_loaded_rows()
    {
        Assert.Equal("350.50", Preview().TextFor(Text("{Sum(Amount)}", "N2")));
    }

    [Fact]
    public void The_number_format_of_the_element_is_applied()
    {
        Assert.Equal("100.00", Preview().TextFor(Text("{Amount}", "N2")));
    }

    [Fact]
    public void Plain_text_stays_as_it_is()
    {
        Assert.Equal("Invoice", Preview().TextFor(Text("Invoice")));
    }

    [Fact]
    public void Without_rows_the_design_text_is_kept()
    {
        var p = DesignPreview.From(new ReportDefinition(), new List<Dictionary<string, object?>>());
        Assert.Equal("{Name}", p.TextFor(Text("{Name}")));
    }

    [Fact]
    public void A_broken_expression_never_throws()
    {
        var text = Preview().TextFor(Text("x {1 +}"));
        Assert.False(string.IsNullOrEmpty(text));
    }

    [Fact]
    public void Pictures_and_lines_keep_their_text()
    {
        var item = new ReportItem { Kind = ItemKind.Line, Text = "{Name}" };
        Assert.Equal("{Name}", Preview().TextFor(item));
    }

    [Fact]
    public void Barcode_values_are_evaluated()
    {
        var item = new ReportItem { Kind = ItemKind.Barcode, Text = "{Name}" };
        Assert.Equal("Ravi", Preview().TextFor(item));
    }

    [Fact]
    public void Loading_the_sample_data_source_gives_rows_and_a_cap()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.Sample;

        var p = DesignPreview.Load(def, null, maxRows: 5);

        Assert.Equal(5, p.RowCount);
        Assert.NotEqual("{Customer}", p.TextFor(Text("{Customer}")));
    }
}
