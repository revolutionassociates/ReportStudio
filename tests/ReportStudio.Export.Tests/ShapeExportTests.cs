using System.Drawing;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using ReportStudio.Export.Pdf;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Ellipse and rounded box (requirement 34, first part) in the preview picture, the PDF and HTML.</summary>
public class ShapeExportTests
{
    // ---- PDF: curves ----

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public void A_filled_ellipse_is_four_curves_in_one_closed_path()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillShape(10, 20, 100, 60, 50, 30, Color.Red);

        var content = page.Content;
        Assert.Equal(4, Count(content, " c\n"));
        Assert.Contains("1 0 0 rg\n", content);
        Assert.Contains("60 20 m\n", content);          // the top middle: x + half the width
        Assert.Contains("h\nf\n", content);              // closed, then filled
    }

    [Fact]
    public void A_rounded_box_has_straight_sides_between_its_four_corners()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillShape(0, 0, 100, 40, 8, 8, Color.Blue);

        var content = page.Content;
        Assert.Equal(4, Count(content, " c\n"));
        Assert.Equal(4, Count(content, " l\n"));
        Assert.Contains("8 0 m\n", content);             // starts after the top-left corner
        Assert.Contains("92 0 l\n", content);            // runs along the top to the top-right corner
    }

    [Fact]
    public void The_curve_control_points_follow_the_quarter_ellipse_constant()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillShape(0, 0, 100, 100, 50, 50, Color.Black);

        // 50 * 0.5522847 = 27.6142: the first curve leaves the top middle (50, 0) towards the right edge (100, 50)
        Assert.Contains("77.6142 0 100 22.3858 100 50 c\n", page.Content);
    }

    [Fact]
    public void The_outline_uses_the_colour_and_width_and_strokes_the_path()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.StrokeShape(10, 20, 100, 60, 50, 30, Color.FromArgb(0, 128, 0), 2);

        var content = page.Content;
        Assert.Contains("0 0.502 0 RG 2 w\n", content);
        Assert.Equal(4, Count(content, " c\n"));
        Assert.EndsWith("h\nS\n", content);
    }

    [Fact]
    public void Nothing_is_drawn_for_a_transparent_colour_a_zero_width_line_or_no_area()
    {
        var page = new PdfDocument("t").AddPage(794, 1123);

        page.FillShape(0, 0, 100, 60, 50, 30, Color.Transparent);
        page.StrokeShape(0, 0, 100, 60, 50, 30, Color.Black, 0);
        page.FillShape(0, 0, 0, 60, 0, 30, Color.Black);
        page.StrokeShape(0, 0, 100, 0, 50, 0, Color.Black, 1);

        Assert.Equal("", page.Content);
    }

    // ---- HTML ----

    private static string Html(RenderedItem item)
    {
        var result = new ReportResult { ReportName = "Shapes" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.Add(item);
        result.Pages.Add(page);
        return HtmlExporter.Build(result);
    }

    [Fact]
    public void An_ellipse_is_a_box_with_fully_round_edges()
    {
        var html = Html(new RenderedItem { Kind = ItemKind.Ellipse, X = 10, Y = 20, Width = 100, Height = 50, BackColor = "#ffe0b2", BorderWidth = 1, BorderColor = "#555555" });

        Assert.Contains("border-radius:50%", html);
        Assert.Contains("background:#ffe0b2", html);
        Assert.Contains("border:1px solid #555555", html);
    }

    [Fact]
    public void A_rounded_box_gets_its_radius_and_a_too_large_radius_is_cut_back()
    {
        Assert.Contains("border-radius:12px", Html(new RenderedItem { Kind = ItemKind.Rectangle, Width = 100, Height = 50, CornerRadius = 12 }));
        Assert.Contains("border-radius:25px", Html(new RenderedItem { Kind = ItemKind.Rectangle, Width = 100, Height = 50, CornerRadius = 999 }));
    }

    [Fact]
    public void A_plain_box_has_no_radius()
    {
        Assert.DoesNotContain("border-radius", Html(new RenderedItem { Kind = ItemKind.Rectangle, Width = 100, Height = 50, BorderWidth = 1 }));
    }

    // ---- the picture (preview, image export) ----

    private static Bitmap Draw(RenderedItem item)
    {
        var bmp = new Bitmap(100, 60);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        GdiPageRenderer.DrawItem(g, item);
        return bmp;
    }

    private static bool IsRed(Color c) => c.R > 200 && c.G < 60 && c.B < 60;

    [Fact]
    public void An_ellipse_fills_the_middle_and_leaves_the_corners_empty()
    {
        using var bmp = Draw(new RenderedItem { Kind = ItemKind.Ellipse, Width = 100, Height = 60, BackColor = "#FF0000" });

        Assert.True(IsRed(bmp.GetPixel(50, 30)), "centre");
        Assert.True(IsRed(bmp.GetPixel(50, 3)), "top middle");
        Assert.False(IsRed(bmp.GetPixel(2, 2)), "top-left corner");
        Assert.False(IsRed(bmp.GetPixel(97, 57)), "bottom-right corner");
    }

    [Fact]
    public void A_rounded_box_cuts_its_corners_but_keeps_its_edges()
    {
        using var bmp = Draw(new RenderedItem { Kind = ItemKind.Rectangle, Width = 100, Height = 60, BackColor = "#FF0000", CornerRadius = 20 });

        Assert.False(IsRed(bmp.GetPixel(1, 1)), "corner");
        Assert.False(IsRed(bmp.GetPixel(98, 58)), "opposite corner");
        Assert.True(IsRed(bmp.GetPixel(50, 2)), "top edge");
        Assert.True(IsRed(bmp.GetPixel(2, 30)), "left edge");
        Assert.True(IsRed(bmp.GetPixel(50, 30)), "centre");
    }

    [Fact]
    public void A_box_without_a_radius_is_still_square()
    {
        using var bmp = Draw(new RenderedItem { Kind = ItemKind.Rectangle, Width = 100, Height = 60, BackColor = "#FF0000" });

        Assert.True(IsRed(bmp.GetPixel(1, 1)));
        Assert.True(IsRed(bmp.GetPixel(98, 58)));
    }

    [Fact]
    public void A_shape_with_no_area_draws_nothing_and_does_not_fail()
    {
        using var bmp = Draw(new RenderedItem { Kind = ItemKind.Ellipse, Width = 0, Height = 0, BackColor = "#FF0000", BorderWidth = 1 });

        Assert.False(IsRed(bmp.GetPixel(0, 0)));
    }
}
