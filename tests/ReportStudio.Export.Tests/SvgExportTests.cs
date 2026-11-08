using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>SVG pictures (requirement 35, second part) in the preview picture, the PDF and the HTML.</summary>
public class SvgExportTests
{
    // 20 x 10, all red.
    private const string Red =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\" viewBox=\"0 0 20 10\"><rect width=\"20\" height=\"10\" fill=\"#ff0000\"/></svg>";

    private const string Unsafe =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><image href=\"http://example.com/a.png\" width=\"20\" height=\"10\"/></svg>";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ---- the size it is drawn at ----

    [Fact]
    public void The_drawing_is_fitted_inside_the_box_keeping_its_proportions()
    {
        Assert.Equal((100, 50), SvgRaster.FitSize(20, 10, 100, 100, 1));
        Assert.Equal((50, 100), SvgRaster.FitSize(10, 20, 100, 100, 1));
        Assert.Equal((200, 100), SvgRaster.FitSize(20, 10, 100, 100, 2));
    }

    [Fact]
    public void A_drawing_without_a_size_takes_the_proportions_of_the_box()
    {
        Assert.Equal((200, 100), SvgRaster.FitSize(0, 0, 100, 50, 2));
        Assert.Equal((100, 50), SvgRaster.FitSize(double.NaN, 5, 100, 50, 1));
    }

    [Fact]
    public void The_size_never_goes_below_one_or_above_four_thousand_pixels()
    {
        Assert.Equal((4000, 4000), SvgRaster.FitSize(1, 1, 10000, 10000, 1));
        Assert.Equal((4000, 2000), SvgRaster.FitSize(2, 1, 10000, 10000, 1));
        var (w, h) = SvgRaster.FitSize(1000, 1, 1, 1, 1);
        Assert.True(w >= 1 && h >= 1);
    }

    // ---- turning it into a picture ----

    [Fact]
    public void An_svg_becomes_a_png_and_other_pictures_pass_through_untouched()
    {
        var png = SvgRaster.Resolve(Bytes(Red), 100, 100, 1);
        Assert.True(png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47, "PNG header");

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        Assert.Same(jpeg, SvgRaster.Resolve(jpeg, 100, 100, 1));
    }

    [Fact]
    public void An_unsafe_svg_gives_nothing()
    {
        Assert.Empty(SvgRaster.Resolve(Bytes(Unsafe), 100, 100, 1));
    }

    [Fact]
    public void A_broken_svg_gives_nothing()
    {
        Assert.Empty(SvgRaster.Resolve(Bytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect"), 100, 100, 1));
    }

    // ---- the picture (preview, image export, printing) ----

    private static Bitmap Draw(string svg)
    {
        var page = new RenderedPage { Number = 1, Width = 100, Height = 100 };
        page.Items.Add(new RenderedItem { Kind = ItemKind.Image, X = 0, Y = 0, Width = 100, Height = 100, Image = Bytes(svg) });
        var bmp = new Bitmap(100, 100);
        using var g = Graphics.FromImage(bmp);
        GdiPageRenderer.Draw(g, page, 1f);
        return bmp;
    }

    [Fact]
    public void The_preview_draws_the_svg_centred_in_its_box()
    {
        using var bmp = Draw(Red);

        var middle = bmp.GetPixel(50, 50);
        Assert.True(middle.R > 240 && middle.G < 20 && middle.B < 20, $"the drawing is red in the middle, got {middle}");
        var above = bmp.GetPixel(50, 5);                       // 100 x 50 drawing: rows 25 to 75 only
        Assert.True(above.R > 245 && above.G > 245 && above.B > 245, $"white above the drawing, got {above}");
    }

    [Fact]
    public void The_preview_leaves_an_unsafe_svg_out()
    {
        using var bmp = Draw(Unsafe);

        var middle = bmp.GetPixel(50, 50);
        Assert.True(middle.R > 245 && middle.G > 245 && middle.B > 245, $"nothing drawn, got {middle}");
    }

    // ---- PDF and HTML ----

    private static ReportResult Result(string svg)
    {
        var result = new ReportResult { ReportName = "Svg" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.Add(new RenderedItem { Kind = ItemKind.Image, X = 10, Y = 10, Width = 100, Height = 100, Image = Bytes(svg) });
        result.Pages.Add(page);
        return result;
    }

    private static string Pdf(string svg)
    {
        using var ms = new MemoryStream();
        PdfExporter.Export(Result(svg), ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void The_pdf_holds_the_svg_as_a_picture()
    {
        Assert.Matches(@"/Subtype\s*/Image", Pdf(Red));
    }

    [Fact]
    public void The_pdf_leaves_an_unsafe_svg_out()
    {
        Assert.DoesNotMatch(@"/Subtype\s*/Image", Pdf(Unsafe));
    }

    [Fact]
    public void Html_embeds_the_svg_itself()
    {
        var html = HtmlExporter.Build(Result(Red));

        Assert.Contains("src=\"data:image/svg+xml;base64," + Convert.ToBase64String(Bytes(Red)), html);
    }

    [Fact]
    public void Html_leaves_an_unsafe_svg_out()
    {
        var html = HtmlExporter.Build(Result(Unsafe));

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("example.com", html);
    }
}
