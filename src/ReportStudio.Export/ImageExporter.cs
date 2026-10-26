using System.Drawing;
using System.Drawing.Imaging;
using ReportStudio.Core.Layout;

namespace ReportStudio.Export;

public enum ImageFormatKind { Png, Jpeg, Bmp, Gif, Tiff }

public static class ImageExporter
{
    public static string Extension(ImageFormatKind k) => k switch
    {
        ImageFormatKind.Jpeg => ".jpg",
        ImageFormatKind.Bmp => ".bmp",
        ImageFormatKind.Gif => ".gif",
        ImageFormatKind.Tiff => ".tiff",
        _ => ".png"
    };

    private static ImageFormat Format(ImageFormatKind k) => k switch
    {
        ImageFormatKind.Jpeg => ImageFormat.Jpeg,
        ImageFormatKind.Bmp => ImageFormat.Bmp,
        ImageFormatKind.Gif => ImageFormat.Gif,
        ImageFormatKind.Tiff => ImageFormat.Tiff,
        _ => ImageFormat.Png
    };

    /// <summary>Renders one page to a bitmap. dpi 96 = 1:1, 192 = double resolution.</summary>
    public static Bitmap RenderPage(RenderedPage page, int dpi = 150)
    {
        float scale = dpi / 96f;
        var bmp = new Bitmap((int)Math.Ceiling(page.Width * scale), (int)Math.Ceiling(page.Height * scale));
        bmp.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(bmp);
        GdiPageRenderer.Draw(g, page, scale);
        return bmp;
    }

    /// <summary>
    /// Saves every page. A single-page report is written to <paramref name="path"/> exactly;
    /// multi-page reports get "_page1", "_page2"... appended. Returns the files written.
    /// </summary>
    public static List<string> Export(ReportResult report, string path, ImageFormatKind format, int dpi = 150)
    {
        var written = new List<string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Extension(format);
        Directory.CreateDirectory(dir);

        foreach (var page in report.Pages)
        {
            var file = report.Pages.Count == 1
                ? Path.Combine(dir, name + ext)
                : Path.Combine(dir, $"{name}_page{page.Number}{ext}");
            using var bmp = RenderPage(page, dpi);
            bmp.Save(file, Format(format));
            written.Add(file);
        }
        return written;
    }
}
