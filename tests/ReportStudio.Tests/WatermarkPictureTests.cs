using System.Globalization;
using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Picture watermark (requirement 35, third part): the settings, the sizing rule and the engine.</summary>
public class WatermarkPictureTests
{
    // A real 1x1 PNG.
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><rect width=\"20\" height=\"10\" fill=\"#ff0000\"/></svg>";

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---- the settings ----

    [Fact]
    public void A_new_watermark_has_no_picture_and_half_the_page_width()
    {
        var w = new ReportDefinition().Watermark;

        Assert.Equal("", w.ImageBase64);
        Assert.Equal(50, w.ImageSize);
    }

    [Fact]
    public void Normalizing_trims_the_picture_and_keeps_the_width_in_range()
    {
        var w = new WatermarkSettings { ImageBase64 = "  abc \n", ImageSize = 500 };
        w.Normalize();
        Assert.Equal("abc", w.ImageBase64);
        Assert.Equal(100, w.ImageSize);

        w.ImageSize = 1;
        w.Normalize();
        Assert.Equal(5, w.ImageSize);

        w.ImageSize = double.NaN;
        w.ImageBase64 = null!;
        w.Normalize();
        Assert.Equal(50, w.ImageSize);
        Assert.Equal("", w.ImageBase64);
    }

    [Fact]
    public void The_picture_survives_saving()
    {
        var def = new ReportDefinition();
        def.Watermark.ImageBase64 = PngBase64;
        def.Watermark.ImageSize = 70;

        var back = ReportDefinition.FromJson(def.ToJson()).Watermark;

        Assert.Equal(PngBase64, back.ImageBase64);
        Assert.Equal(70, back.ImageSize);
    }

    [Fact]
    public void An_old_file_with_a_text_watermark_only_loads_without_a_picture()
    {
        var def = ReportDefinition.FromJson("""{ "Name": "Old", "Watermark": { "Text": "DRAFT" } }""");

        Assert.Equal("DRAFT", def.Watermark.Text);
        Assert.Equal("", def.Watermark.ImageBase64);
        Assert.Equal(50, def.Watermark.ImageSize);
    }

    // ---- the size it is drawn at ----

    [Fact]
    public void The_picture_is_the_chosen_share_of_the_page_width_with_its_own_proportions()
    {
        var (w, h) = WatermarkLayout.PictureSize(100, 50, 800, 1000, 0, 50);

        Assert.Equal(400, w, 6);
        Assert.Equal(200, h, 6);
    }

    [Fact]
    public void A_picture_that_fits_when_turned_is_not_made_smaller()
    {
        var (w, h) = WatermarkLayout.PictureSize(100, 50, 800, 1000, 90, 100);      // 800 x 400 turned upright is 400 x 800

        Assert.Equal(800, w, 6);
        Assert.Equal(400, h, 6);
    }

    [Fact]
    public void A_picture_turned_so_that_it_would_overhang_the_page_is_made_smaller()
    {
        var (w, h) = WatermarkLayout.PictureSize(100, 100, 800, 1000, 45, 100);

        Assert.Equal(800 / Math.Sqrt(2), w, 3);
        Assert.Equal(800 / Math.Sqrt(2), h, 3);

        var (tw, th) = WatermarkLayout.PictureSize(100, 200, 800, 1000, 90, 100);   // 800 x 1600, turned 1600 x 800: half size
        Assert.Equal(400, tw, 6);
        Assert.Equal(800, th, 6);
    }

    [Fact]
    public void A_turn_by_a_full_circle_or_a_negative_angle_sizes_the_same_way()
    {
        var upright = WatermarkLayout.PictureSize(100, 50, 800, 1000, 0, 100);

        Assert.Equal(upright.Width, WatermarkLayout.PictureSize(100, 50, 800, 1000, 360, 100).Width, 6);
        Assert.Equal(WatermarkLayout.PictureSize(100, 50, 800, 1000, 30, 100).Width,
            WatermarkLayout.PictureSize(100, 50, 800, 1000, -30, 100).Width, 6);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 50)]
    public void A_picture_with_no_size_gives_nothing(double pictureWidth, double pictureHeight)
    {
        Assert.Equal((0.0, 0.0), WatermarkLayout.PictureSize(pictureWidth, pictureHeight, 800, 1000, 0, 50));
    }

    [Fact]
    public void A_percentage_outside_the_range_is_brought_into_it()
    {
        Assert.Equal(WatermarkLayout.PictureSize(100, 100, 800, 1000, 0, 100), WatermarkLayout.PictureSize(100, 100, 800, 1000, 0, 5000));
        Assert.Equal(WatermarkLayout.PictureSize(100, 100, 800, 1000, 0, 5), WatermarkLayout.PictureSize(100, 100, 800, 1000, 0, 1));
        Assert.Equal(WatermarkLayout.PictureSize(100, 100, 800, 1000, 0, 50), WatermarkLayout.PictureSize(100, 100, 800, 1000, 0, double.NaN));
    }

    // ---- in a report ----

    private static ReportDefinition Definition(string text, string image, double opacity = 0.3)
    {
        var def = new ReportDefinition { Name = "Marked" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{A}", Width = 100, Height = 20 });
        def.Watermark.Text = text;
        def.Watermark.ImageBase64 = image;
        def.Watermark.ImageSize = 60;
        def.Watermark.Opacity = opacity;
        return def;
    }

    private static ReportResult Run(ReportDefinition def, int rows = 1) =>
        ReportEngine.Run(def, null,
            Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = i }),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

    [Fact]
    public void A_picture_alone_makes_a_watermark_on_every_page()
    {
        var result = Run(Definition("", PngBase64), rows: 100);

        Assert.True(result.Pages.Count >= 2, "the report needs more than one page for this test");
        Assert.All(result.Pages, page =>
        {
            var mark = Assert.IsType<RenderedWatermark>(page.Watermark);
            Assert.Equal(Convert.FromBase64String(PngBase64), mark.Image);
            Assert.Equal("", mark.Text);
            Assert.Equal(60, mark.ImageSize);
        });
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Text_and_picture_can_be_used_together()
    {
        var mark = Run(Definition("DRAFT", PngBase64)).Pages[0].Watermark!;

        Assert.Equal("DRAFT", mark.Text);
        Assert.NotNull(mark.Image);
    }

    [Fact]
    public void Text_alone_has_no_picture()
    {
        var mark = Run(Definition("DRAFT", "")).Pages[0].Watermark!;

        Assert.Equal("DRAFT", mark.Text);
        Assert.Null(mark.Image);
    }

    [Fact]
    public void Neither_text_nor_picture_means_no_watermark()
    {
        Assert.All(Run(Definition("", "")).Pages, page => Assert.Null(page.Watermark));
    }

    [Fact]
    public void A_see_through_nothing_prints_no_watermark_even_with_a_picture()
    {
        Assert.All(Run(Definition("DRAFT", PngBase64, opacity: 0)).Pages, page => Assert.Null(page.Watermark));
    }

    [Fact]
    public void A_plain_svg_drawing_is_accepted()
    {
        var mark = Run(Definition("", Base64(Svg))).Pages[0].Watermark!;

        Assert.Equal(Encoding.UTF8.GetBytes(Svg), mark.Image);
    }

    [Theory]
    [InlineData("this is not a picture at all")]
    [InlineData("aGVsbG8gd29ybGQ=")]                                    // base64 of "hello world"
    public void A_picture_that_is_not_one_is_left_out_with_one_warning(string image)
    {
        var result = Run(Definition("DRAFT", image), rows: 100);

        Assert.True(result.Pages.Count >= 2);
        Assert.All(result.Pages, page => Assert.Null(page.Watermark!.Image));         // the text is still there
        Assert.Single(result.Warnings, w => w.Contains("watermark picture was left out"));
    }

    [Fact]
    public void An_unsafe_svg_is_left_out()
    {
        var result = Run(Definition("", Base64("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x()</script></svg>")));

        Assert.Null(result.Pages[0].Watermark);                                       // no text either: nothing to print
        Assert.Contains(result.Warnings, w => w.Contains("watermark picture was left out"));
    }
}
