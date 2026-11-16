using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReportStudio.Core.Layout;

namespace ReportStudio.Export;

/// <summary>
/// Writes an XPS document (.xps), the fixed-layout format Windows opens in its XPS Viewer and can print to the XPS printer.
/// Every report page becomes one fixed page filled with a picture of the page at <c>dpi</c> (150 by default), so the page looks
/// exactly as it does in the viewer: fonts, shapes, barcodes, pictures and watermarks included. Text in the file is not
/// selectable, because XPS needs every font embedded in an obfuscated font part; use PDF when text has to stay searchable.
/// </summary>
public static class XpsExporter
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";
    private const string NsXps = "http://schemas.microsoft.com/xps/2005/06";
    private const string NsRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string NsTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void Export(ReportResult report, string path, int dpi = 150)
    {
        using var file = File.Create(path);
        Export(report, file, dpi);
    }

    public static byte[] ToBytes(ReportResult report, int dpi = 150)
    {
        using var ms = new MemoryStream();
        Export(report, ms, dpi);
        return ms.ToArray();
    }

    /// <summary>Writes the document to a stream (left open). <paramref name="dpi"/> is limited to 72..600.</summary>
    public static void Export(ReportResult report, Stream stream, int dpi = 150)
    {
        ArgumentNullException.ThrowIfNull(report);
        dpi = Math.Clamp(dpi, 72, 600);

        var pages = report.Pages.Count > 0
            ? report.Pages.ToList()
            : new List<RenderedPage> { new() { Number = 1, Width = 794, Height = 1123 } };

        // Built in memory first, then copied: the zip writer needs a seekable stream.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(zip, "[Content_Types].xml", ContentTypes());
            AddText(zip, "_rels/.rels", RootRels());
            AddText(zip, "FixedDocumentSequence.fdseq", Sequence());
            AddText(zip, "Documents/1/FixedDocument.fdoc", Document(pages));

            for (int i = 0; i < pages.Count; i++)
            {
                int n = i + 1;
                var page = pages[i];
                double w = page.Width > 0 ? page.Width : 794;
                double h = page.Height > 0 ? page.Height : 1123;

                AddText(zip, $"Documents/1/Pages/{n}.fpage", FixedPage(n, w, h));
                AddText(zip, $"Documents/1/Pages/_rels/{n}.fpage.rels", PageRels(n));

                using var bmp = ImageExporter.RenderPage(page, dpi);
                using var png = new MemoryStream();
                bmp.Save(png, ImageFormat.Png);
                // A PNG is already compressed.
                var entry = zip.CreateEntry($"Resources/Images/{n}.png", CompressionLevel.NoCompression);
                using var target = entry.Open();
                target.Write(png.GetBuffer(), 0, (int)png.Length);
            }
        }
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var target = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        target.Write(bytes, 0, bytes.Length);
    }

    private static string ContentTypes() =>
        Declaration +
        $"<Types xmlns=\"{NsTypes}\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
        "<Default Extension=\"fdseq\" ContentType=\"application/vnd.ms-package.xps-fixeddocumentsequence+xml\"/>" +
        "<Default Extension=\"fdoc\" ContentType=\"application/vnd.ms-package.xps-fixeddocument+xml\"/>" +
        "<Default Extension=\"fpage\" ContentType=\"application/vnd.ms-package.xps-fixedpage+xml\"/>" +
        "</Types>";

    private static string RootRels() =>
        Declaration +
        $"<Relationships xmlns=\"{NsRels}\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.microsoft.com/xps/2005/06/fixedrepresentation\" Target=\"FixedDocumentSequence.fdseq\"/>" +
        "</Relationships>";

    private static string Sequence() =>
        Declaration +
        $"<FixedDocumentSequence xmlns=\"{NsXps}\"><DocumentReference Source=\"/Documents/1/FixedDocument.fdoc\"/></FixedDocumentSequence>";

    private static string Document(List<RenderedPage> pages)
    {
        var sb = new StringBuilder(Declaration);
        sb.Append($"<FixedDocument xmlns=\"{NsXps}\">");
        for (int i = 0; i < pages.Count; i++)
        {
            double w = pages[i].Width > 0 ? pages[i].Width : 794;
            double h = pages[i].Height > 0 ? pages[i].Height : 1123;
            sb.Append($"<PageContent Source=\"/Documents/1/Pages/{i + 1}.fpage\" Width=\"{Num(w)}\" Height=\"{Num(h)}\"/>");
        }
        sb.Append("</FixedDocument>");
        return sb.ToString();
    }

    private static string FixedPage(int n, double w, double h)
    {
        string width = Num(w), height = Num(h);
        return Declaration +
            $"<FixedPage xmlns=\"{NsXps}\" xml:lang=\"und\" Width=\"{width}\" Height=\"{height}\">" +
            $"<Path Data=\"M 0,0 L {width},0 L {width},{height} L 0,{height} Z\">" +
            "<Path.Fill>" +
            $"<ImageBrush ImageSource=\"/Resources/Images/{n}.png\" Viewbox=\"0,0,{width},{height}\" ViewboxUnits=\"Absolute\" " +
            $"Viewport=\"0,0,{width},{height}\" ViewportUnits=\"Absolute\"/>" +
            "</Path.Fill>" +
            "</Path>" +
            "</FixedPage>";
    }

    private static string PageRels(int n) =>
        Declaration +
        $"<Relationships xmlns=\"{NsRels}\">" +
        $"<Relationship Id=\"rId1\" Type=\"http://schemas.microsoft.com/xps/2005/06/required-resource\" Target=\"/Resources/Images/{n}.png\"/>" +
        "</Relationships>";
}
