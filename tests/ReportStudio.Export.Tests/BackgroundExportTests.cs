using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Page background (requirement 34, fourth part) in the PDF, HTML and the preview picture.</summary>
public class BackgroundExportTests
{
    private static RenderedBackground Back(string color = "#FFF8E1", string gradient = "", GradientDirection direction = GradientDirection.TopToBottom) =>
        new() { Color = color, GradientColor = gradient, GradientDirection = direction };

    private static ReportResult Result(RenderedBackground? background, int pages = 1, RenderedWatermark? watermark = null)
    {
        var result = new ReportResult { ReportName = "Background" };
        for (int i = 1; i <= pages; i++)
        {
            var page = new RenderedPage { Number = i, Width = 794, Height = 1123, Background = background, Watermark = watermark };
            page.Items.Add(new RenderedItem { Kind = ItemKind.Rectangle, X = 10, Y = 10, Width = 50, Height = 20, BackColor = "#FF0000" });
            result.Pages.Add(page);
        }
        return result;
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    // ---- PDF ----

    private static byte[] Exported(ReportResult result)
    {
        using var ms = new MemoryStream();
        PdfExporter.Export(result, ms);
        return ms.ToArray();
    }

    /// <summary>Everything the page content streams say, unpacked: the file keeps them compressed.</summary>
    private static string Contents(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var all = new StringBuilder();
        foreach (Match m in Regex.Matches(text, "stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
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
    public void A_single_colour_fills_the_whole_page()
    {
        var contents = Contents(Exported(Result(Back("#FFF8E1"))));

        Assert.Contains("1 0.9725 0.8824 rg", contents);
        Assert.Contains("0 0 794 1123 re", contents);
    }

    [Fact]
    public void A_single_colour_makes_no_shading()
    {
        Assert.DoesNotContain("/ShadingType", Encoding.Latin1.GetString(Exported(Result(Back()))));
    }

    [Fact]
    public void A_gradient_is_one_shading_clipped_to_the_page()
    {
        var bytes = Exported(Result(Back("#FF0000", "#0000FF"), pages: 3));

        Assert.Contains("q\n0 0 794 1123 re\nW n\n794 0 0 1123 0 0 cm\n/Sh1 sh\nQ\n", Contents(bytes));
        var file = Encoding.Latin1.GetString(bytes);
        Assert.Equal(1, Count(file, "/ShadingType 2"));          // three pages share one definition
        Assert.Contains("/Coords [0 0 0 1]", file);
    }

    [Fact]
    public void A_left_to_right_gradient_runs_along_x()
    {
        var file = Encoding.Latin1.GetString(Exported(Result(Back("#FF0000", "#0000FF", GradientDirection.LeftToRight))));

        Assert.Contains("/Coords [0 0 1 0]", file);
    }

    [Fact]
    public void The_background_is_painted_before_the_watermark_and_the_items()
    {
        var mark = new RenderedWatermark { Text = "DRAFT", Angle = 45, Opacity = 0.3, FontSize = 72 };
        var contents = Contents(Exported(Result(Back("#FF0000", "#0000FF"), watermark: mark)));

        int background = contents.IndexOf("/Sh1 sh", StringComparison.Ordinal);
        int watermark = contents.IndexOf("/Gs1 gs", StringComparison.Ordinal);
        int item = contents.IndexOf("50 20 re", StringComparison.Ordinal);       // the red box

        Assert.True(background >= 0 && watermark > background && item > watermark, $"order was {background}, {watermark}, {item}");
    }

    [Fact]
    public void No_background_or_an_unreadable_colour_leaves_the_page_plain()
    {
        Assert.DoesNotContain("0 0 794 1123 re", Contents(Exported(Result(null))));
        Assert.DoesNotContain("0 0 794 1123 re", Contents(Exported(Result(Back("not-a-colour")))));
    }

    [Fact]
    public void An_unreadable_gradient_colour_gives_the_single_colour()
    {
        var bytes = Exported(Result(Back("#FF0000", "not-a-colour")));

        Assert.Contains("0 0 794 1123 re", Contents(bytes));
        Assert.DoesNotContain("/ShadingType", Encoding.Latin1.GetString(bytes));
    }

    // ---- HTML ----

    private static string Html(RenderedBackground? background, int pages = 1) => HtmlExporter.Build(Result(background, pages));

    private static string PageTags(string html) =>
        string.Join("\n", Regex.Matches(html, "<section class=\"page\"[^>]*>").Select(m => m.Value));

    [Fact]
    public void Html_paints_a_single_colour_on_the_page_and_asks_browsers_to_print_it()
    {
        var tag = PageTags(Html(Back("#FFF8E1")));

        Assert.Contains("background:#fff8e1", tag);
        Assert.Contains("print-color-adjust:exact", tag);
    }

    [Fact]
    public void Html_paints_a_gradient_in_the_chosen_direction()
    {
        Assert.Contains("background:linear-gradient(to bottom,#ff0000,#0000ff)", PageTags(Html(Back("#FF0000", "#0000FF"))));
        Assert.Contains("background:linear-gradient(to right,#ff0000,#0000ff)",
            PageTags(Html(Back("#FF0000", "#0000FF", GradientDirection.LeftToRight))));
    }

    [Fact]
    public void Html_gives_every_page_the_background()
    {
        Assert.Equal(3, Count(PageTags(Html(Back(), pages: 3)), "background:#fff8e1"));
    }

    [Fact]
    public void Html_leaves_the_page_white_without_a_usable_colour()
    {
        Assert.DoesNotContain("print-color-adjust", PageTags(Html(null)));
        Assert.DoesNotContain("print-color-adjust", PageTags(Html(Back("not-a-colour"))));
    }

    [Fact]
    public void Html_falls_back_to_the_single_colour_when_the_gradient_colour_is_unreadable()
    {
        var tag = PageTags(Html(Back("#FF0000", "not-a-colour")));

        Assert.Contains("background:#ff0000", tag);
        Assert.DoesNotContain("linear-gradient", tag);
    }

    // ---- the picture (preview, image export, printing) ----

    private static Bitmap Draw(RenderedBackground? background, float scale = 1f, bool withBox = false)
    {
        var page = new RenderedPage { Number = 1, Width = 200, Height = 200, Background = background };
        if (withBox)
            page.Items.Add(new RenderedItem { Kind = ItemKind.Rectangle, X = 80, Y = 80, Width = 40, Height = 40, BackColor = "#00FF00" });
        var bmp = new Bitmap((int)(200 * scale), (int)(200 * scale));
        using var g = Graphics.FromImage(bmp);
        GdiPageRenderer.Draw(g, page, scale);
        return bmp;
    }

    private static bool Red(Color c) => c.R > 240 && c.G < 20 && c.B < 20;
    private static bool White(Color c) => c.R > 245 && c.G > 245 && c.B > 245;
    private static bool MoreRed(Color c) => c.R > c.B + 100;
    private static bool MoreBlue(Color c) => c.B > c.R + 100;

    [Fact]
    public void A_single_colour_covers_the_page_edge_to_edge()
    {
        using var bmp = Draw(Back("#FF0000"));

        Assert.True(Red(bmp.GetPixel(0, 0)), "top-left");
        Assert.True(Red(bmp.GetPixel(199, 199)), "bottom-right");
        Assert.True(Red(bmp.GetPixel(100, 100)), "centre");
    }

    [Fact]
    public void A_scale_that_is_not_a_whole_number_leaves_no_white_edge()
    {
        using var bmp = Draw(Back("#FF0000"), scale: 1.5f);

        Assert.Equal(300, bmp.Width);
        Assert.True(Red(bmp.GetPixel(299, 299)), "last pixel");
        Assert.True(Red(bmp.GetPixel(299, 0)), "last column");
        Assert.True(Red(bmp.GetPixel(0, 299)), "last row");
    }

    [Fact]
    public void A_gradient_changes_colour_in_the_chosen_direction()
    {
        using var down = Draw(Back("#FF0000", "#0000FF"));
        Assert.True(MoreRed(down.GetPixel(100, 3)), "top");
        Assert.True(MoreBlue(down.GetPixel(100, 196)), "bottom");

        using var across = Draw(Back("#FF0000", "#0000FF", GradientDirection.LeftToRight));
        Assert.True(MoreRed(across.GetPixel(3, 100)), "left");
        Assert.True(MoreBlue(across.GetPixel(196, 100)), "right");
    }

    [Fact]
    public void No_background_or_an_unreadable_colour_leaves_the_page_white()
    {
        using var none = Draw(null);
        using var bad = Draw(Back("not-a-colour"));

        Assert.True(White(none.GetPixel(100, 100)));
        Assert.True(White(bad.GetPixel(100, 100)));
    }

    [Fact]
    public void An_unreadable_gradient_colour_gives_the_single_colour()
    {
        using var bmp = Draw(Back("#FF0000", "not-a-colour"));

        Assert.True(Red(bmp.GetPixel(100, 3)));
        Assert.True(Red(bmp.GetPixel(100, 196)));
    }

    [Fact]
    public void Items_are_drawn_over_the_background()
    {
        using var bmp = Draw(Back("#FF0000"), withBox: true);

        var c = bmp.GetPixel(100, 100);
        Assert.True(c.G > 200 && c.R < 60, $"the green box covers the page colour, got {c}");
        Assert.True(Red(bmp.GetPixel(10, 10)), "the page colour is still there around it");
    }
}
