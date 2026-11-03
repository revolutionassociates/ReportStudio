using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Hyperlinks and bookmarks (requirement 27).</summary>
public class LinkTests
{
    // ---- which addresses are allowed ----

    [Theory]
    [InlineData("https://example.com/a", LinkKind.External, "https://example.com/a")]
    [InlineData("  HTTP://example.com  ", LinkKind.External, "http://example.com")]
    [InlineData("mailto:hr@example.com", LinkKind.External, "mailto:hr@example.com")]
    [InlineData("tel:+911234567890", LinkKind.External, "tel:+911234567890")]
    [InlineData("#Section1", LinkKind.Internal, "#Section1")]
    public void Allowed_addresses_are_kept(string text, LinkKind kind, string value)
    {
        Assert.Equal((kind, value), LinkTarget.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("\\\\server\\share\\a.pdf")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("ftp://example.com")]
    [InlineData("https://")]
    [InlineData("https:example.com")]
    [InlineData("mailto:")]
    [InlineData("#")]
    [InlineData("example.com")]
    [InlineData("https://example.com/a\nb")]
    public void Everything_else_is_refused(string text)
    {
        Assert.Equal((LinkKind.None, ""), LinkTarget.Parse(text));
    }

    [Fact]
    public void Spaces_and_accents_are_percent_encoded_but_existing_encoding_stays()
    {
        Assert.Equal("https://example.com/a%20b/caf%C3%A9?q=1%202&x=%41", LinkTarget.Clean("https://example.com/a b/café?q=1 2&x=%41"));
    }

    [Fact]
    public void A_very_long_address_is_refused()
    {
        Assert.Equal("", LinkTarget.Clean("https://example.com/" + new string('a', 2100)));
    }

    // ---- the engine ----

    private static Dictionary<string, object?> Row(string name, string code) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Name"] = name, ["Code"] = code };

    private static ReportResult Run(ReportItem item, params Dictionary<string, object?>[] rows)
    {
        var def = new ReportDefinition { Name = "Links" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        def.Detail.Items.Add(item);
        return ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    private static ReportItem Cell(string link = "", string bookmark = "") =>
        new() { Kind = ItemKind.Text, Text = "{Name}", Width = 100, Height = 18, Link = link, Bookmark = bookmark };

    [Fact]
    public void A_link_is_worked_out_for_each_row()
    {
        var result = Run(Cell(link: "https://example.com/emp/{Code}"), Row("A", "E1"), Row("B", "E 2"));

        var links = result.Pages.SelectMany(p => p.Items).Select(i => i.Link).ToList();

        Assert.Equal(new[] { "https://example.com/emp/E1", "https://example.com/emp/E%202" }, links);
    }

    [Fact]
    public void A_bookmark_is_worked_out_for_each_row_and_found_by_name()
    {
        var result = Run(Cell(bookmark: "Emp-{Code}"), Row("A", "E1"), Row("B", "E2"));

        var marks = result.FindBookmarks();

        Assert.Equal(new[] { "Emp-E1", "Emp-E2" }, marks.Select(m => m.Name));
        Assert.Equal(0, result.FindBookmark("#emp-e2")!.PageIndex);   // case does not matter, # is optional
        Assert.Null(result.FindBookmark("Emp-E9"));
    }

    [Fact]
    public void A_bookmark_used_twice_keeps_its_first_place()
    {
        var result = Run(Cell(bookmark: "Same"), Row("A", "1"), Row("B", "2"));

        var mark = Assert.Single(result.FindBookmarks());
        var first = result.Pages[0].Items.First(i => i.Bookmark == "Same");
        Assert.Equal(first.Y, mark.Y);
    }

    [Fact]
    public void A_refused_address_gives_no_link_and_one_warning()
    {
        var result = Run(Cell(link: "javascript:alert({Code})"), Row("A", "1"));

        Assert.All(result.Pages.SelectMany(p => p.Items), i => Assert.Equal("", i.Link));
        Assert.Contains(result.Warnings, w => w.Contains("not allowed"));
    }

    [Fact]
    public void Items_without_link_or_bookmark_are_unchanged()
    {
        var result = Run(Cell(), Row("A", "1"));

        var item = result.Pages.SelectMany(p => p.Items).Single();
        Assert.Equal("", item.Link);
        Assert.Equal("", item.Bookmark);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Link_and_bookmark_survive_saving_and_copying()
    {
        var def = new ReportDefinition { Name = "Save" };
        def.Detail.Items.Add(Cell("#Top", "Here"));

        var loaded = ReportDefinition.FromJson(def.ToJson());
        var copy = def.Clone();

        Assert.Equal("#Top", loaded.Detail.Items[0].Link);
        Assert.Equal("Here", loaded.Detail.Items[0].Bookmark);
        Assert.Equal("#Top", copy.Detail.Items[0].Link);
        Assert.Equal("Here", copy.Detail.Items[0].Bookmark);
    }
}
