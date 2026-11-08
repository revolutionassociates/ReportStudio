using System.Drawing;
using System.Drawing.Imaging;
using ReportStudio.Core.Layout;
using Svg;

namespace ReportStudio.Export;

/// <summary>
/// Draws an SVG picture as a PNG for the outputs that need a raster (preview, image export, printing, PDF).
/// Only SVG that passed <see cref="ImageData.IsSafeSvg"/> is opened: a drawing that links to other files or web addresses,
/// or carries scripts, is left out rather than followed. HTML embeds the SVG itself and does not use this.
/// </summary>
internal static class SvgRaster
{
    private const int MaxSide = 4000;

    /// <summary>
    /// The bytes ready for a raster reader: a PNG when <paramref name="picture"/> is SVG, the same bytes otherwise.
    /// An SVG that cannot be drawn gives an empty array (nothing is printed).
    /// </summary>
    public static byte[] Resolve(byte[] picture, double boxWidth, double boxHeight, double scale)
    {
        if (!ImageData.LooksLikeSvg(picture)) return picture;
        return ToPng(picture, boxWidth, boxHeight, scale) ?? Array.Empty<byte>();
    }

    /// <summary>
    /// The size to draw at: the drawing's own proportions, fitted inside the box times <paramref name="scale"/>
    /// (no drawn size known: the box's proportions), never below 1 or above 4000 pixels on a side.
    /// </summary>
    internal static (int Width, int Height) FitSize(double drawingWidth, double drawingHeight, double boxWidth, double boxHeight, double scale)
    {
        double bw = Math.Max(1, boxWidth * scale), bh = Math.Max(1, boxHeight * scale);
        if (!(drawingWidth > 0) || !(drawingHeight > 0) || double.IsInfinity(drawingWidth) || double.IsInfinity(drawingHeight))
        {
            drawingWidth = bw; drawingHeight = bh;
        }
        double ratio = Math.Min(bw / drawingWidth, bh / drawingHeight);
        double w = drawingWidth * ratio, h = drawingHeight * ratio;
        double over = Math.Max(w, h) / MaxSide;
        if (over > 1) { w /= over; h /= over; }
        return ((int)Math.Max(1, Math.Round(w)), (int)Math.Max(1, Math.Round(h)));
    }

    private static byte[]? ToPng(byte[] svg, double boxWidth, double boxHeight, double scale)
    {
        if (!ImageData.IsSafeSvg(svg)) return null;
        try
        {
            using var input = new MemoryStream(svg);
            var document = SvgDocument.Open<SvgDocument>(input);
            var size = document.GetDimensions();
            var (w, h) = FitSize(size.Width, size.Height, boxWidth, boxHeight, scale);

            using var bitmap = document.Draw(w, h);
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken or unsupported drawing: leave the box empty, like any unreadable picture.
            return null;
        }
    }
}
