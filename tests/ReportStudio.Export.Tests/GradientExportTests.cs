using System.Drawing;
using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using ReportStudio.Export.Pdf;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Gradient fill (requirement 34, second part) in the PDF, HTML and the preview picture.</summary>
public class GradientExportTests
{
    private static int Count(string text, string part) => text.Split(part).Length - 1;

    // ---- PDF: the shading definitions ----

    [Fact]
    public void The_same_colours_and_direction_share_one_shading_and_others_get_their_own()
    {
        var doc = new PdfDocument("t");

        var a = doc.Shading(Color.Red, Color.Blue, false);
        Assert.Equal(a, doc.Shading(Color.Red, Color.Blue, false));
        Assert.NotEqual(a, doc.Shading(Color.Red, Color.Blue, true));
        Assert.NotEqual(a, doc.Shading(Color.Blue, Color.Red, false));
        Assert.NotEqual(a, doc.Shading(Color.Red, Color.Green, false));
    }

    [Fact]
    public void A_plain_box_is_clipped_to_a_rectangle_and_the_shading_is_moved_and_scaled_to_it()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillGradient("Sh1", 10, 20, 100, 60, 0, 0);

        Assert.Equal("q\n10 20 100 60 re\nW n\n100 0 0 60 10 20 cm\n/Sh1 sh\nQ\n", page.Content);
    }

    [Fact]
    public void An_ellipse_is_clipped_to_its_curves()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillGradient("Sh1", 0, 0, 100, 100, 50, 50);

        var content = page.Content;
        Assert.StartsWith("q\n50 0 m\n", content);
        Assert.Equal(4, Count(content, " c\n"));
        Assert.EndsWith("h\nW n\n100 0 0 100 0 0 cm\n/Sh1 sh\nQ\n", content);
    }

    [Fact]
    public void A_gradient_with_no_area_draws_nothing()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillGradient("Sh1", 0, 0, 0, 60, 0, 0);
        page.FillGradient("Sh1", 0, 0, 100, 0, 0, 0);

        Assert.Equal("", page.Content);
    }

    private static string Saved(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, null);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void The_file_holds_an_axial_shading_between_the_two_colours()
    {
        var doc = new PdfDocument("t");
        var page = doc.AddPage(794, 1123);
        page.FillGradient(doc.Shading(Color.Red, Color.Blue, false), 0, 0, 100, 60, 0, 0);

        var file = Saved(doc);

        Assert.Contains("/ShadingType 2", file);
        Assert.Contains("/ColorSpace /DeviceRGB", file);
        Assert.Contains("/Coords [0 0 0 1]", file);        // top to bottom: along y
        Assert.Contains("/C0 [1 0 0]", file);
        Assert.Contains("/C1 [0 0 1]", file);
        Assert.Contains("/Extend [true true]", file);
        Assert.Contains("/Shading <<", file);
    }

    [Fact]
    public void Left_to_right_runs_along_x()
    {
        var doc = new PdfDocument("t");
        var page = doc.AddPage(794, 1123);
        page.FillGradient(doc.Shading(Color.Red, Color.Blue, true), 0, 0, 100, 60, 0, 0);

        Assert.Contains("/Coords [0 0 1 0]", Saved(doc));
    }

    [Fact]
    public void A_file_without_gradients_has_no_shading_resources()
    {
        var doc = new PdfDocument("t");
        doc.AddPage(794, 1123).FillRectangle(0, 0, 10, 10, Color.Red);

        Assert.DoesNotContain("/Shading", Saved(doc));
    }

    private static string Exported(params RenderedItem[] items)
    {
        var result = new ReportResult { ReportName = "Gradients" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.AddRange(items);
        result.Pages.Add(page);
        using var ms = new MemoryStream();
        PdfExporter.Export(result, ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static RenderedItem Shape(ItemKind kind, string back, string second, GradientDirection direction = GradientDirection.TopToBottom) =>
        new() { Kind = kind, X = 10, Y = 10, Width = 100, Height = 50, BackColor = back, GradientColor = second, GradientDirection = direction };

    [Fact]
    public void Exported_items_with_the_same_gradient_share_one_definition()
    {
        var file = Exported(
            Shape(ItemKind.Rectangle, "#FF0000", "#0000FF"),
            Shape(ItemKind.Ellipse, "#FF0000", "#0000FF"));

        Assert.Equal(1, Count(file, "/ShadingType 2"));
    }

    [Fact]
    public void Exported_items_with_different_gradients_each_get_one()
    {
        var file = Exported(
            Shape(ItemKind.Rectangle, "#FF0000", "#0000FF"),
            Shape(ItemKind.Rectangle, "#FF0000", "#0000FF", GradientDirection.LeftToRight),
            Shape(ItemKind.Rectangle, "#00FF00", "#0000FF"));

        Assert.Equal(3, Count(file, "/ShadingType 2"));
    }

    [Fact]
    public void A_solid_fill_or_an_unreadable_gradient_colour_makes_no_shading()
    {
        Assert.DoesNotContain("/ShadingType", Exported(Shape(ItemKind.Rectangle, "#FF0000", "")));
        Assert.DoesNotContain("/ShadingType", Exported(Shape(ItemKind.Rectangle, "#FF0000", "not-a-colour")));
    }

    // ---- HTML ----

    private static string Html(RenderedItem item)
    {
        var result = new ReportResult { ReportName = "Gradients" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.Add(item);
        result.Pages.Add(page);
        return HtmlExporter.Build(result);
    }

    [Fact]
    public void Html_paints_a_linear_gradient_in_the_chosen_direction()
    {
        Assert.Contains("background:linear-gradient(to bottom,#ff0000,#0000ff)", Html(Shape(ItemKind.Rectangle, "#FF0000", "#0000FF")));
        Assert.Contains("background:linear-gradient(to right,#ff0000,#0000ff)", Html(Shape(ItemKind.Ellipse, "#FF0000", "#0000FF", GradientDirection.LeftToRight)));
    }

    [Fact]
    public void A_text_box_can_have_a_gradient_too()
    {
        var item = Shape(ItemKind.Text, "#FF0000", "#0000FF");
        item.Text = "Net pay";

        Assert.Contains("linear-gradient(to bottom,#ff0000,#0000ff)", Html(item));
    }

    [Fact]
    public void Html_keeps_a_solid_fill_when_there_is_no_usable_gradient()
    {
        var plain = Html(Shape(ItemKind.Rectangle, "#FF0000", ""));
        var broken = Html(Shape(ItemKind.Rectangle, "#FF0000", "not-a-colour"));

        Assert.Contains("background:#ff0000", plain);
        Assert.Contains("background:#ff0000", broken);
        Assert.DoesNotContain("linear-gradient", plain + broken);
    }

    [Fact]
    public void Html_gives_a_barcode_a_solid_fill_even_when_a_gradient_colour_is_set()
    {
        var item = Shape(ItemKind.QrCode, "#FF0000", "#0000FF");
        item.Text = "https://reas.host/";

        Assert.DoesNotContain("linear-gradient", Html(item));
    }

    // ---- the picture (preview, image export) ----

    private static Bitmap Draw(RenderedItem item)
    {
        var bmp = new Bitmap(100, 60);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        item.X = 0; item.Y = 0; item.Width = 100; item.Height = 60;
        GdiPageRenderer.DrawItem(g, item);
        return bmp;
    }

    private static bool MoreRed(Color c) => c.R > c.B + 100;
    private static bool MoreBlue(Color c) => c.B > c.R + 100;

    [Fact]
    public void Top_to_bottom_changes_colour_down_the_box_but_not_across_it()
    {
        using var bmp = Draw(Shape(ItemKind.Rectangle, "#FF0000", "#0000FF"));

        Assert.True(MoreRed(bmp.GetPixel(50, 2)), "top");
        Assert.True(MoreBlue(bmp.GetPixel(50, 57)), "bottom");
        Assert.Equal(bmp.GetPixel(5, 30).ToArgb(), bmp.GetPixel(95, 30).ToArgb());
    }

    [Fact]
    public void Left_to_right_changes_colour_across_the_box_but_not_down_it()
    {
        using var bmp = Draw(Shape(ItemKind.Rectangle, "#FF0000", "#0000FF", GradientDirection.LeftToRight));

        Assert.True(MoreRed(bmp.GetPixel(2, 30)), "left");
        Assert.True(MoreBlue(bmp.GetPixel(97, 30)), "right");
        Assert.Equal(bmp.GetPixel(50, 5).ToArgb(), bmp.GetPixel(50, 55).ToArgb());
    }

    [Fact]
    public void A_gradient_ellipse_leaves_its_corners_empty()
    {
        using var bmp = Draw(Shape(ItemKind.Ellipse, "#FF0000", "#0000FF"));

        Assert.False(MoreRed(bmp.GetPixel(2, 2)) || MoreBlue(bmp.GetPixel(2, 2)), "corner");
        Assert.True(MoreRed(bmp.GetPixel(50, 5)), "top");
        Assert.True(MoreBlue(bmp.GetPixel(50, 54)), "bottom");
    }

    [Fact]
    public void An_unreadable_gradient_colour_falls_back_to_the_solid_fill()
    {
        using var bmp = Draw(Shape(ItemKind.Rectangle, "#FF0000", "not-a-colour"));

        Assert.True(MoreRed(bmp.GetPixel(50, 2)));
        Assert.True(MoreRed(bmp.GetPixel(50, 57)));
    }
}
