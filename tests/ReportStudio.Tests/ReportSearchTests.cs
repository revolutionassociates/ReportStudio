using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Searching the finished report (requirement 29).</summary>
public class ReportSearchTests
{
    private static RenderedItem Text(string text, double x = 0, double y = 0, ItemKind kind = ItemKind.Text) =>
        new() { Kind = kind, Text = text, X = x, Y = y, Width = 100, Height = 20 };

    private static ReportResult Report(params RenderedItem[][] pages)
    {
        var report = new ReportResult();
        foreach (var items in pages)
        {
            var page = new RenderedPage { Number = report.Pages.Count + 1 };
            page.Items.AddRange(items);
            report.Pages.Add(page);
        }
        return report;
    }

    [Fact]
    public void Finds_text_on_every_page_in_reading_order()
    {
        var report = Report(
            new[] { Text("Total 10", y: 50), Text("Name: Ravi", y: 10) },
            new[] { Text("ravi again", y: 5) });

        var hits = ReportSearch.Find(report, "ravi");

        Assert.Equal(new[] { (0, "Name: Ravi"), (1, "ravi again") }, hits.Select(h => (h.PageIndex, h.Item.Text)));
    }

    [Fact]
    public void Items_on_the_same_line_are_ordered_left_to_right()
    {
        var report = Report(new[] { Text("b one", x: 200), Text("a one", x: 10) });

        var hits = ReportSearch.Find(report, "one");

        Assert.Equal(new[] { "a one", "b one" }, hits.Select(h => h.Item.Text));
    }

    [Fact]
    public void Case_matters_only_when_asked()
    {
        var report = Report(new[] { Text("Invoice"), Text("invoice") });

        Assert.Equal(2, ReportSearch.Find(report, "INVOICE").Count);
        Assert.Equal(new[] { "invoice" }, ReportSearch.Find(report, "invoice", matchCase: true).Select(h => h.Item.Text));
    }

    [Fact]
    public void A_phrase_is_found_where_the_text_wrapped_or_has_extra_spaces()
    {
        var report = Report(new[] { Text("Sharma\nTraders  Pvt"), Text("Other") });

        Assert.Single(ReportSearch.Find(report, "sharma traders pvt"));
        Assert.Single(ReportSearch.Find(report, "  Sharma   Traders "));
    }

    [Fact]
    public void An_empty_search_finds_nothing()
    {
        var report = Report(new[] { Text("x") });

        Assert.Empty(ReportSearch.Find(report, ""));
        Assert.Empty(ReportSearch.Find(report, "   "));
        Assert.Empty(ReportSearch.Find(report, null));
    }

    [Fact]
    public void Barcodes_and_empty_items_are_not_searched()
    {
        var report = Report(new[] { Text("12345", kind: ItemKind.Barcode), Text("", y: 5), Text("1234", y: 9) });

        var hits = ReportSearch.Find(report, "123");

        Assert.Equal(new[] { "1234" }, hits.Select(h => h.Item.Text));
    }

    [Fact]
    public void Nothing_found_gives_an_empty_list()
    {
        Assert.Empty(ReportSearch.Find(Report(new[] { Text("abc") }), "zzz"));
        Assert.Empty(ReportSearch.Find(new ReportResult(), "abc"));
    }
}
