using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using ReportStudio.Export.Pdf;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Watermark (requirement 34, third part) in the PDF, HTML and the preview picture.</summary>
public class WatermarkExportTests
{
    private static RenderedWatermark Mark(string text = "DRAFT", double angle = 45, double opacity = 0.3, double size = 72, bool bold = true,
        string color = "#9CA3AF") =>
        new() { Text = text, Angle = angle, Opacity = opacity, FontSize = size, Bold = bold, Color = color };

    private static ReportResult Result(RenderedWatermark? mark, int pages = 1, double width = 794, double height = 1123)
    {
        var result = new ReportResult { ReportName = "Watermarked" };
        for (int i = 1; i <= pages; i++)
        {
            var page = new RenderedPage { Number = i, Width = width, Height = height, Watermark = mark };
            page.Items.Add(new RenderedItem { Kind = ItemKind.Rectangle, X = 10, Y = 10, Width = 50, Height = 20, BackColor = "#FF0000" });
            result.Pages.Add(page);
        }
        return result;
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    // ---- PDF: turning and see-through ----

    [Fact]
    public void A_quarter_turn_is_written_with_clean_numbers()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.PushRotation(100, 200, 90);

        Assert.Equal("q\n0 -1 1 0 100 200 cm\n", page.Content);
    }

    [Fact]
    public void No_turn_is_the_identity_and_never_writes_minus_zero()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.PushRotation(100, 200, 0);

        Assert.Equal("q\n1 0 0 1 100 200 cm\n", page.Content);
    }

    [Fact]
    public void A_counter_clockwise_turn_of_45_degrees_lifts_the_right_hand_end()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.PushRotation(100, 200, 45);

        // the x direction (1, 0) becomes (cos, -sin): to the right and up the page (y grows downward)
        Assert.Equal("q\n0.7071 -0.7071 0.7071 0.7071 100 200 cm\n", page.Content);
    }

    [Fact]
    public void A_negative_angle_turns_the_other_way()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.PushRotation(0, 0, -90);

        Assert.Equal("q\n0 1 -1 0 0 0 cm\n", page.Content);
    }

    [Fact]
    public void Opacity_and_the_end_of_the_turn_are_written_as_operators()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.SetOpacity("Gs1");
        page.PopRotation();

        Assert.Equal("/Gs1 gs\nQ\n", page.Content);
    }

    [Fact]
    public void The_same_opacity_shares_one_graphics_state()
    {
        var doc = new PdfDocument("t");

        var a = doc.Opacity(0.3);
        Assert.Equal(a, doc.Opacity(0.3));
        Assert.Equal(a, doc.Opacity(0.304));                 // the same whole percent
        Assert.NotEqual(a, doc.Opacity(0.5));
        Assert.Equal(doc.Opacity(1), doc.Opacity(7));        // cut to the range
        Assert.Equal(doc.Opacity(0), doc.Opacity(double.NaN));
    }

    private static string Saved(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, null);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void The_file_lists_each_see_through_setting_for_fills_and_strokes()
    {
        var doc = new PdfDocument("t");
        doc.AddPage(794, 1123).SetOpacity(doc.Opacity(0.3));

        var file = Saved(doc);

        Assert.Contains("/ExtGState", file);
        Assert.Contains("/ca 0.3", file);
        Assert.Contains("/CA 0.3", file);
    }

    [Fact]
    public void A_file_without_see_through_settings_has_no_graphics_states()
    {
        var doc = new PdfDocument("t");
        doc.AddPage(794, 1123).FillRectangle(0, 0, 10, 10, Color.Red);

        Assert.DoesNotContain("/ExtGState", Saved(doc));
    }

    // ---- PDF: the exported page ----

    private static byte[] Exported(ReportResult result)
    {
        using var ms = new MemoryStream();
        PdfExporter.Export(result, ms);
        return ms.ToArray();
    }

    private static string Squash(string text) => new(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    [Fact]
    public void The_watermark_text_is_in_the_pdf_and_the_file_declares_its_opacity()
    {
        var bytes = Exported(Result(Mark("CONFIDENTIAL")));

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Contains("CONFIDENTIAL", Squash(pdf.GetPage(1).Text));
        Assert.Contains("/ca 0.3", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void Every_page_gets_the_watermark()
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Exported(Result(Mark("DRAFT"), pages: 3)));

        Assert.Equal(3, pdf.NumberOfPages);
        for (int i = 1; i <= 3; i++)
            Assert.Contains("DRAFT", Squash(pdf.GetPage(i).Text));
    }

    [Fact]
    public void No_watermark_leaves_the_page_as_it_was()
    {
        var file = Encoding.Latin1.GetString(Exported(Result(null)));

        Assert.DoesNotContain("/ExtGState", file);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_watermark_text_draws_nothing(string text)
    {
        var file = Encoding.Latin1.GetString(Exported(Result(Mark(text))));

        Assert.DoesNotContain("/ExtGState", file);
    }

    [Fact]
    public void A_fully_see_through_watermark_draws_nothing()
    {
        var file = Encoding.Latin1.GetString(Exported(Result(Mark(opacity: 0))));

        Assert.DoesNotContain("/ExtGState", file);
    }

    [Fact]
    public void A_long_text_is_made_smaller_to_stay_inside_the_page_and_is_centred()
    {
        const double pageWidth = 794, pageHeight = 1123;
        var mark = Mark("A VERY LONG WATERMARK TEXT THAT CANNOT POSSIBLY FIT AT THIS SIZE", angle: 0);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Exported(Result(mark, width: pageWidth, height: pageHeight)));
        var letters = pdf.GetPage(1).Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();

        Assert.NotEmpty(letters);
        double left = letters.Min(l => l.GlyphRectangle.Left), right = letters.Max(l => l.GlyphRectangle.Right);
        Assert.True(left >= 0 && right <= pageWidth * 0.75, $"the text runs from {left} to {right} points");
        Assert.InRange((left + right) / 2, pageWidth * 0.75 / 2 - 2, pageWidth * 0.75 / 2 + 2);   // centred, to within a couple of points                      // centred, to within a few points
    }

    // ---- HTML ----

    private static string Html(RenderedWatermark? mark, int pages = 1) => HtmlExporter.Build(Result(mark, pages));

    [Fact]
    public void Html_turns_the_text_and_makes_it_faint()
    {
        var html = Html(Mark("DRAFT"));

        Assert.Contains(">DRAFT</span>", html);
        Assert.Contains("transform:rotate(-45deg)", html);     // CSS turns clockwise, the setting counter-clockwise
        Assert.Contains("opacity:0.3", html);
        Assert.Contains("color:#9ca3af", html);
        Assert.Contains("font-weight:700", html);
        Assert.Contains("pointer-events:none", html);           // it must not block clicks on links underneath
    }

    [Fact]
    public void Html_follows_the_settings()
    {
        var html = Html(Mark(angle: -30, opacity: 0.15, bold: false, color: "#FF0000"));

        Assert.Contains("transform:rotate(30deg)", html);
        Assert.Contains("opacity:0.15", html);
        Assert.Contains("font-weight:400", html);
        Assert.Contains("color:#ff0000", html);
    }

    [Fact]
    public void Html_encodes_the_text()
    {
        var html = Html(Mark("A & B <i>"));

        Assert.Contains(">A &amp; B &lt;i&gt;</span>", html);
        Assert.DoesNotContain("<i>", html);
    }

    [Fact]
    public void Html_puts_the_watermark_behind_the_items()
    {
        var html = Html(Mark());

        Assert.True(html.IndexOf("transform:rotate", StringComparison.Ordinal) < html.IndexOf("background:#ff0000", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_writes_one_watermark_per_page()
    {
        Assert.Equal(3, Count(Html(Mark(), pages: 3), "transform:rotate"));
    }

    [Fact]
    public void Html_writes_nothing_without_a_usable_watermark()
    {
        Assert.DoesNotContain("transform:rotate", Html(null));
        Assert.DoesNotContain("transform:rotate", Html(Mark("")));
        Assert.DoesNotContain("transform:rotate", Html(Mark(opacity: 0)));
    }

    [Fact]
    public void Html_makes_a_very_long_text_smaller()
    {
        var html = Html(Mark(new string('W', 100), angle: 0, size: 72));

        var size = double.Parse(Regex.Match(html, @"font-size:([0-9.]+)px").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(size < 96, $"the wanted size is 96 px, got {size}");        // 72 pt = 96 px
    }

    [Fact]
    public void Html_keeps_the_wanted_size_for_a_short_text()
    {
        var html = Html(Mark("OK", angle: 0, size: 72));

        Assert.Contains("font-size:96px", html);
    }

    // ---- the picture (preview, image export, printing) ----

    private static Bitmap Draw(RenderedWatermark? mark)
    {
        var page = new RenderedPage { Number = 1, Width = 200, Height = 200, Watermark = mark };
        var bmp = new Bitmap(200, 200);
        using var g = Graphics.FromImage(bmp);
        GdiPageRenderer.Draw(g, page);
        return bmp;
    }

    private static bool Dark(Color c) => c.R < 100 && c.G < 100 && c.B < 100;
    private static bool White(Color c) => c.R > 245 && c.G > 245 && c.B > 245;

    [Fact]
    public void A_page_without_a_watermark_stays_white()
    {
        using var bmp = Draw(null);

        Assert.True(White(bmp.GetPixel(100, 100)));
    }

    [Fact]
    public void The_text_is_drawn_at_the_centre_of_the_page()
    {
        using var bmp = Draw(Mark("I", angle: 0, opacity: 1, size: 100, color: "#000000"));

        Assert.True(Dark(bmp.GetPixel(100, 100)), "the stroke of the letter is at the centre");
        Assert.True(White(bmp.GetPixel(30, 100)), "left of it");
        Assert.True(White(bmp.GetPixel(100, 5)), "above it");
    }

    [Fact]
    public void A_quarter_turn_lays_the_upright_letter_on_its_side()
    {
        using var bmp = Draw(Mark("I", angle: 90, opacity: 1, size: 100, color: "#000000"));

        Assert.True(Dark(bmp.GetPixel(100, 100)), "centre");
        Assert.True(Dark(bmp.GetPixel(80, 100)), "the letter now reaches sideways from the centre");
        Assert.True(White(bmp.GetPixel(100, 40)), "and no longer reaches up");
    }

    [Fact]
    public void Half_opacity_blends_with_the_white_page()
    {
        using var bmp = Draw(Mark("I", angle: 0, opacity: 0.5, size: 100, color: "#000000"));

        var c = bmp.GetPixel(100, 100);
        Assert.InRange(c.R, 100, 155);                       // about half way between black and white
    }

    [Fact]
    public void A_fully_see_through_watermark_leaves_the_page_white()
    {
        using var bmp = Draw(Mark("I", angle: 0, opacity: 0, size: 100, color: "#000000"));

        Assert.True(White(bmp.GetPixel(100, 100)));
    }

    [Fact]
    public void Items_are_drawn_over_the_watermark()
    {
        var page = new RenderedPage { Number = 1, Width = 200, Height = 200, Watermark = Mark("I", angle: 0, opacity: 1, size: 100, color: "#000000") };
        page.Items.Add(new RenderedItem { Kind = ItemKind.Rectangle, X = 80, Y = 80, Width = 40, Height = 40, BackColor = "#00FF00" });
        using var bmp = new Bitmap(200, 200);
        using (var g = Graphics.FromImage(bmp)) GdiPageRenderer.Draw(g, page);

        var c = bmp.GetPixel(100, 100);
        Assert.True(c.G > 200 && c.R < 60, $"the green box covers the letter, got {c}");
    }
}
