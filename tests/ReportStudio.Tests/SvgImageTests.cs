using System.Globalization;
using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>SVG pictures (requirement 35, second part): which SVG is accepted, and how it reaches the engine.</summary>
public class SvgImageTests
{
    private const string Plain =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\" viewBox=\"0 0 20 10\"><rect width=\"20\" height=\"10\" fill=\"#ff0000\"/></svg>";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ---- recognising an SVG ----

    [Theory]
    [InlineData(Plain)]
    [InlineData("  \r\n" + Plain)]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + Plain)]
    [InlineData("<?xml version=\"1.0\"?><!-- made by a tool -->\n<!-- twice -->" + Plain)]
    [InlineData("<SVG xmlns=\"http://www.w3.org/2000/svg\"></SVG>")]
    public void Text_that_starts_like_a_drawing_is_an_svg(string text)
    {
        Assert.True(ImageData.LooksLikeSvg(Bytes(text)));
        Assert.True(ImageData.IsPicture(Bytes(text)));
    }

    [Fact]
    public void A_byte_order_mark_is_allowed()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Plain)).ToArray();

        Assert.True(ImageData.LooksLikeSvg(bytes));
        Assert.True(ImageData.IsSafeSvg(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("<html><body><svg></svg></body></html>")]
    [InlineData("<svgx></svgx>")]
    public void Other_text_is_not_an_svg(string text)
    {
        Assert.False(ImageData.LooksLikeSvg(Bytes(text)));
        Assert.False(ImageData.IsPicture(Bytes(text)));
    }

    [Fact]
    public void Nothing_and_pictures_are_not_an_svg()
    {
        Assert.False(ImageData.LooksLikeSvg(null));
        Assert.False(ImageData.LooksLikeSvg(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }));
    }

    [Fact]
    public void A_huge_svg_is_refused()
    {
        var big = Bytes("<svg>" + new string(' ', ImageData.MaxSvgBytes) + "</svg>");

        Assert.False(ImageData.LooksLikeSvg(big));
    }

    // ---- what makes an SVG unsafe ----

    [Fact]
    public void A_plain_drawing_is_safe_and_so_are_links_inside_it_and_pictures_held_in_it()
    {
        Assert.True(ImageData.IsSafeSvg(Bytes(Plain)));
        Assert.True(ImageData.IsSafeSvg(Bytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">" +
            "<defs><linearGradient id=\"g\"><stop offset=\"0\"/></linearGradient><rect id=\"r\" width=\"5\" height=\"5\"/></defs>" +
            "<use xlink:href=\"#r\"/><rect fill=\"url(#g)\" width=\"5\" height=\"5\"/><rect fill=\"url('#g')\" width=\"5\" height=\"5\"/>" +
            "<rect fill=\"url( #g)\" width=\"5\" height=\"5\"/>" +
            "<image href=\"data:image/png;base64,iVBORw0KGgo=\" width=\"5\" height=\"5\"/></svg>")));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><SCRIPT src=\"x.js\"></SCRIPT></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect onclick=\"x()\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><div/></foreignObject></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"http://example.com/a.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><image xlink:href=\"file:///etc/passwd\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"logo.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\" http://example.com/a.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:url(http://example.com/g)\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import url(x.css);</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"><rect/></a></svg>")]
    public void An_svg_that_reaches_outside_the_drawing_is_not_safe(string text)
    {
        Assert.True(ImageData.LooksLikeSvg(Bytes(text)));
        Assert.False(ImageData.IsSafeSvg(Bytes(text)));
        Assert.False(ImageData.IsPicture(Bytes(text)));
    }

    [Fact]
    public void A_drawing_with_a_doctype_or_entities_is_refused()
    {
        var text = "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"b\">]><svg xmlns=\"http://www.w3.org/2000/svg\"/>";

        Assert.False(ImageData.IsPicture(Bytes(text)));
        Assert.False(ImageData.IsSafeSvg(Bytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><!ENTITY a \"b\"></svg>")));
    }

    // ---- from a field ----

    [Fact]
    public void A_field_may_hold_the_markup_or_its_base64()
    {
        Assert.Equal(Bytes(Plain), ImageData.FromText(Plain));
        Assert.Equal(Bytes(Plain), ImageData.FromText(Convert.ToBase64String(Bytes(Plain))));
        Assert.Equal(Bytes(Plain), ImageData.FromText("data:image/svg+xml;base64," + Convert.ToBase64String(Bytes(Plain))));
        Assert.Equal(Bytes(Plain), ImageData.FromValue(Bytes(Plain)));
    }

    [Fact]
    public void An_unsafe_svg_in_a_field_gives_nothing()
    {
        var bad = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x()</script></svg>";

        Assert.Null(ImageData.FromText(bad));
        Assert.Null(ImageData.FromText(Convert.ToBase64String(Bytes(bad))));
        Assert.Null(ImageData.FromValue(Bytes(bad)));
    }

    // ---- in a report ----

    private static ReportResult Run(string imagePath, object? value, string? baseDirectory = null)
    {
        var def = new ReportDefinition { Name = "Drawings" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem { Name = "Logo", Kind = ItemKind.Image, ImagePath = imagePath, Width = 40, Height = 20 });
        return ReportEngine.Run(def, null,
            new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Logo"] = value } },
            new ReportOptions { Culture = CultureInfo.InvariantCulture, BaseDirectory = baseDirectory });
    }

    private static byte[]? Picture(ReportResult r) => r.Pages[0].Items.First(i => i.Kind == ItemKind.Image).Image;

    [Fact]
    public void A_field_with_an_svg_becomes_the_picture()
    {
        var result = Run("{Logo}", Plain);

        Assert.Equal(Bytes(Plain), Picture(result));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void An_svg_file_is_loaded_and_an_unsafe_one_is_left_out_with_a_warning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-svg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.svg"), Plain);
            File.WriteAllText(Path.Combine(dir, "bad.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x()</script></svg>");

            var good = Run("good.svg", null, dir);
            Assert.Equal(Bytes(Plain), Picture(good));
            Assert.Empty(good.Warnings);

            var bad = Run("bad.svg", null, dir);
            Assert.Null(Picture(bad));
            Assert.Contains(bad.Warnings, w => w.Contains("SVG drawing was left out"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
