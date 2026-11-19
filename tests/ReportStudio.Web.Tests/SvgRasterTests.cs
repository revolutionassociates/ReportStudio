using System.Text;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>The picture-size reader that stands in for the GDI-based one in the web viewer.</summary>
public class SvgRasterTests
{
    private static readonly byte[] Png =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, 0, 3, 0, 0, 0, 5, 8, 6, 0, 0, 0
    };

    private static readonly byte[] Gif = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 7, 0, 9, 0, 0, 0 };

    private static readonly byte[] Jpeg =
    {
        0xFF, 0xD8,
        0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0, 17, 8, 0, 12, 0, 20, 3, 0, 0, 0, 0
    };

    [Fact]
    public void A_png_size_is_read_from_its_header()
    {
        var size = SvgRaster.Dimensions(Png);
        Assert.NotNull(size);
        Assert.Equal(3, size.Value.Width);
        Assert.Equal(5, size.Value.Height);
    }

    [Fact]
    public void A_gif_size_is_read_from_its_header()
    {
        var size = SvgRaster.Dimensions(Gif);
        Assert.NotNull(size);
        Assert.Equal(7, size.Value.Width);
        Assert.Equal(9, size.Value.Height);
    }

    [Fact]
    public void A_jpeg_size_is_read_from_its_frame_header()
    {
        var size = SvgRaster.Dimensions(Jpeg);
        Assert.NotNull(size);
        Assert.Equal(20, size.Value.Width);
        Assert.Equal(12, size.Value.Height);
    }

    [Fact]
    public void An_svg_size_comes_from_its_view_box()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 20\"><rect/></svg>");
        var size = SvgRaster.Dimensions(svg);
        Assert.NotNull(size);
        Assert.Equal(40, size.Value.Width);
        Assert.Equal(20, size.Value.Height);
    }

    [Fact]
    public void An_svg_size_prefers_its_width_and_height()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\" viewBox=\"0 0 40 20\"><rect/></svg>");
        var size = SvgRaster.Dimensions(svg);
        Assert.NotNull(size);
        Assert.Equal(100, size.Value.Width);
        Assert.Equal(50, size.Value.Height);
    }

    [Fact]
    public void Anything_else_has_no_size()
    {
        Assert.Null(SvgRaster.Dimensions(new byte[] { 1, 2, 3 }));
        Assert.Null(SvgRaster.Dimensions(Encoding.UTF8.GetBytes("this is only some text, not a picture")));
    }
}
