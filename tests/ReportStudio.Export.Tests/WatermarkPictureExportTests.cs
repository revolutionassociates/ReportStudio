using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Picture watermark (requirement 35, third part) in the preview picture, the PDF and the HTML.</summary>
public class WatermarkPictureExportTests
{
    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><rect width=\"20\" height=\"10\" fill=\"#ff0000\"/></svg>";

    private const string UnsafeSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><image href=\"http://example.com/a.png\" width=\"20\" height=\"10\"/></svg>";

    /// <summary>A solid red picture twice as wide as it is high.</summary>
    private static byte[] RedPng()
    {
        using var bmp = new Bitmap(20, 10);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Red);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static RenderedWatermark Mark(byte[]? image, double angle = 0, double opacity = 1, double size = 50, string text = "") => new()
    {
        Image = image, Angle = angle, Opacity = opacity, ImageSize = size, Text = text
    };

    private static ReportResult Result(RenderedWatermark? mark, double width = 794, double height = 1123)
    {
        var result = new ReportResult { ReportName = "Marked" };
        result.Pages.Add(new RenderedPage { Number = 1, Width = width, Height = height, Watermark = mark });
        return result;
    }

    // ---- the picture (preview, image export, printing) ----

    private static Bitmap Draw(RenderedWatermark mark)
    {
        var page = new RenderedPage { Number = 1, Width = 200, Height = 200, Watermark = mark };
        var bmp = new Bitmap(200, 200);
        using var g = Graphics.FromImage(bmp);
        GdiPageRenderer.Draw(g, page, 1f);
        return bmp;
    }

    private static bool Red(Color c) => c.R > 240 && c.G < 20 && c.B < 20;
    private static bool White(Color c) => c.R > 245 && c.G > 245 && c.B > 245;

    [Fact]
    public void The_preview_draws_the_picture_centred_at_the_chosen_width()
    {
        using var bmp = Draw(Mark(RedPng()));             // 50% of 200 = 100 wide, 50 high: columns 50-150, rows 75-125

        Assert.True(Red(bmp.GetPixel(100, 100)), "centre");
        Assert.True(Red(bmp.GetPixel(60, 80)), "inside, near the top left");
        Assert.True(White(bmp.GetPixel(100, 60)), "above the picture");
        Assert.True(White(bmp.GetPixel(30, 100)), "left of the picture");
    }

    [Fact]
    public void The_preview_turns_the_picture_about_the_middle_of_the_page()
    {
        using var bmp = Draw(Mark(RedPng(), angle: 90));  // 100 x 50 turned a quarter: columns 75-125, rows 50-150

        Assert.True(Red(bmp.GetPixel(100, 60)), "now covered at the top");
        Assert.True(White(bmp.GetPixel(60, 100)), "no longer covered at the left");
    }

    [Fact]
    public void The_preview_fades_the_picture_by_the_opacity()
    {
        using var bmp = Draw(Mark(RedPng(), opacity: 0.5));

        var c = bmp.GetPixel(100, 100);
        Assert.True(c.R > 240 && c.G > 100 && c.G < 160 && c.B > 100 && c.B < 160, $"half-faded red, got {c}");
    }

    [Fact]
    public void The_preview_leaves_an_unreadable_picture_out()
    {
        using var bmp = Draw(Mark(new byte[] { 1, 2, 3, 4, 5, 6 }));

        Assert.True(White(bmp.GetPixel(100, 100)));
    }

    [Fact]
    public void The_preview_draws_a_plain_svg_and_leaves_an_unsafe_one_out()
    {
        using var good = Draw(Mark(Encoding.UTF8.GetBytes(Svg)));
        using var bad = Draw(Mark(Encoding.UTF8.GetBytes(UnsafeSvg)));

        Assert.True(Red(good.GetPixel(100, 100)));
        Assert.True(White(bad.GetPixel(100, 100)));
    }

    // ---- PDF ----

    private static string PdfFile(ReportResult result)
    {
        using var ms = new MemoryStream();
        PdfExporter.Export(result, ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static string Contents(string file)
    {
        var all = new StringBuilder();
        foreach (Match m in Regex.Matches(file, "stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(Encoding.Latin1.GetBytes(m.Groups[1].Value)), CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.Latin1);
                all.Append(reader.ReadToEnd()).Append('\n');
            }
            catch (InvalidDataException) { /* not a compressed stream: a picture, say */ }
        }
        return all.ToString();
    }

    [Fact]
    public void The_pdf_holds_the_picture_and_draws_it_faded()
    {
        var file = PdfFile(Result(Mark(RedPng(), angle: 45, opacity: 0.3)));

        Assert.Matches(@"/Subtype\s*/Image", file);
        var contents = Contents(file);
        Assert.Contains("/Gs1 gs", contents);
        Assert.Matches(@"\bDo\b", contents);
    }

    [Fact]
    public void The_pdf_draws_the_picture_inside_a_turn()
    {
        var contents = Contents(PdfFile(Result(Mark(RedPng(), angle: 90))));

        Assert.Contains("0 -1 1 0 397 561.5 cm", contents);      // 90 degrees about the middle of the 794 x 1123 page
    }

    [Fact]
    public void The_pdf_has_no_picture_for_a_text_watermark_or_an_unusable_picture()
    {
        Assert.DoesNotMatch(@"/Subtype\s*/Image", PdfFile(Result(new RenderedWatermark { Text = "DRAFT" })));
        Assert.DoesNotMatch(@"/Subtype\s*/Image", PdfFile(Result(Mark(Encoding.UTF8.GetBytes(UnsafeSvg)))));
        Assert.DoesNotMatch(@"/Subtype\s*/Image", PdfFile(Result(Mark(new byte[] { 1, 2, 3, 4, 5, 6 }))));
    }

    // ---- HTML ----

    [Fact]
    public void Html_places_the_picture_at_the_chosen_size_turned_and_faded()
    {
        var html = HtmlExporter.Build(Result(Mark(RedPng(), angle: 45, opacity: 0.3)));

        Assert.Contains("<img alt=\"\" style=\"width:397px;height:198.5px;opacity:0.3;transform:rotate(-45deg)\" src=\"data:image/png;base64,", html);
        Assert.Contains("pointer-events:none", html);
    }

    [Fact]
    public void Html_embeds_a_plain_svg_itself_and_leaves_an_unsafe_one_out()
    {
        Assert.Contains("data:image/svg+xml;base64,", HtmlExporter.Build(Result(Mark(Encoding.UTF8.GetBytes(Svg)))));

        var bad = HtmlExporter.Build(Result(Mark(Encoding.UTF8.GetBytes(UnsafeSvg))));
        Assert.DoesNotContain("<img", bad);
        Assert.DoesNotContain("example.com", bad);
    }

    [Fact]
    public void Html_has_no_picture_for_a_text_watermark_or_an_unreadable_picture()
    {
        Assert.DoesNotContain("<img", HtmlExporter.Build(Result(new RenderedWatermark { Text = "DRAFT" })));
        Assert.DoesNotContain("<img", HtmlExporter.Build(Result(Mark(new byte[] { 1, 2, 3, 4, 5, 6 }))));
    }

    [Fact]
    public void Html_puts_the_picture_before_the_text()
    {
        var html = HtmlExporter.Build(Result(Mark(RedPng(), text: "DRAFT")));

        int picture = html.IndexOf("<img", StringComparison.Ordinal);
        int text = html.IndexOf("DRAFT", StringComparison.Ordinal);
        Assert.True(picture >= 0 && text > picture, $"picture at {picture}, text at {text}");
    }
}
