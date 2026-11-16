using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ReportStudio.Core.Layout;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>XPS output (requirement 44d). The page model is built by hand; only the page bitmaps need GDI+.</summary>
public class XpsExporterTests
{
    private static readonly XNamespace Xps = "http://schemas.microsoft.com/xps/2005/06";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static RenderedPage Page(int number, double width = 794, double height = 1123)
    {
        var page = new RenderedPage { Number = number, Width = width, Height = height };
        page.Items.Add(new RenderedItem
        {
            Kind = ItemKind.Text, X = 10, Y = 20, Width = 200, Height = 30, Text = "Hello", FontSize = 10, FontFamily = "Arial"
        });
        return page;
    }

    private static ReportResult Report(params RenderedPage[] pages)
    {
        var result = new ReportResult { ReportName = "Pay" };
        result.Pages.AddRange(pages);
        return result;
    }

    private static string ReadEntry(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var ms = new MemoryStream();
        using var s = archive.GetEntry(name)!.Open();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static List<string> EntryNames(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public void The_package_has_every_required_part_and_all_xml_is_well_formed()
    {
        var zip = XpsExporter.ToBytes(Report(Page(1)));
        var names = EntryNames(zip);

        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("FixedDocumentSequence.fdseq", names);
        Assert.Contains("Documents/1/FixedDocument.fdoc", names);
        Assert.Contains("Documents/1/Pages/1.fpage", names);
        Assert.Contains("Documents/1/Pages/_rels/1.fpage.rels", names);
        Assert.Contains("Resources/Images/1.png", names);

        foreach (var name in names.Where(n => !n.EndsWith(".png")))
            XDocument.Parse(ReadEntry(zip, name));
    }

    [Fact]
    public void The_content_types_name_the_xps_part_types()
    {
        var doc = XDocument.Parse(ReadEntry(XpsExporter.ToBytes(Report(Page(1))), "[Content_Types].xml"));
        string TypeOf(string ext) => doc.Root!.Elements(Types + "Default").Single(e => (string?)e.Attribute("Extension") == ext)
            .Attribute("ContentType")!.Value;

        Assert.Equal("application/vnd.ms-package.xps-fixeddocumentsequence+xml", TypeOf("fdseq"));
        Assert.Equal("application/vnd.ms-package.xps-fixeddocument+xml", TypeOf("fdoc"));
        Assert.Equal("application/vnd.ms-package.xps-fixedpage+xml", TypeOf("fpage"));
        Assert.Equal("image/png", TypeOf("png"));
    }

    [Fact]
    public void The_root_relationship_points_at_the_document_sequence()
    {
        var doc = XDocument.Parse(ReadEntry(XpsExporter.ToBytes(Report(Page(1))), "_rels/.rels"));
        var rel = Assert.Single(doc.Root!.Elements(Rels + "Relationship"));

        Assert.Equal("http://schemas.microsoft.com/xps/2005/06/fixedrepresentation", (string?)rel.Attribute("Type"));
        Assert.Equal("FixedDocumentSequence.fdseq", (string?)rel.Attribute("Target"));
    }

    [Fact]
    public void The_sequence_refers_to_the_one_document()
    {
        var doc = XDocument.Parse(ReadEntry(XpsExporter.ToBytes(Report(Page(1))), "FixedDocumentSequence.fdseq"));
        var reference = Assert.Single(doc.Root!.Elements(Xps + "DocumentReference"));

        Assert.Equal("/Documents/1/FixedDocument.fdoc", (string?)reference.Attribute("Source"));
    }

    [Fact]
    public void Every_report_page_is_one_fixed_page_in_order()
    {
        var zip = XpsExporter.ToBytes(Report(Page(1), Page(2), Page(3)));
        var doc = XDocument.Parse(ReadEntry(zip, "Documents/1/FixedDocument.fdoc"));
        var sources = doc.Root!.Elements(Xps + "PageContent").Select(e => (string?)e.Attribute("Source")).ToList();

        Assert.Equal(new[]
        {
            "/Documents/1/Pages/1.fpage", "/Documents/1/Pages/2.fpage", "/Documents/1/Pages/3.fpage"
        }, sources);
        Assert.Contains("Documents/1/Pages/3.fpage", EntryNames(zip));
        Assert.Contains("Resources/Images/3.png", EntryNames(zip));
    }

    [Fact]
    public void A_fixed_page_has_the_size_of_its_report_page()
    {
        var zip = XpsExporter.ToBytes(Report(Page(1, 400, 300), Page(2, 300, 400)));

        var first = XDocument.Parse(ReadEntry(zip, "Documents/1/Pages/1.fpage")).Root!;
        var second = XDocument.Parse(ReadEntry(zip, "Documents/1/Pages/2.fpage")).Root!;

        Assert.Equal("400", (string?)first.Attribute("Width"));
        Assert.Equal("300", (string?)first.Attribute("Height"));
        Assert.Equal("300", (string?)second.Attribute("Width"));
        Assert.Equal("400", (string?)second.Attribute("Height"));
    }

    [Fact]
    public void The_page_is_a_rectangle_filled_with_its_picture()
    {
        var zip = XpsExporter.ToBytes(Report(Page(1, 400, 300)));
        var page = XDocument.Parse(ReadEntry(zip, "Documents/1/Pages/1.fpage"));
        var path = Assert.Single(page.Descendants(Xps + "Path"));
        var brush = Assert.Single(page.Descendants(Xps + "ImageBrush"));

        Assert.Equal("M 0,0 L 400,0 L 400,300 L 0,300 Z", (string?)path.Attribute("Data"));
        Assert.Equal("/Resources/Images/1.png", (string?)brush.Attribute("ImageSource"));
        Assert.Equal("0,0,400,300", (string?)brush.Attribute("Viewbox"));
        Assert.Equal("Absolute", (string?)brush.Attribute("ViewboxUnits"));
        Assert.Equal("0,0,400,300", (string?)brush.Attribute("Viewport"));
        Assert.Equal("Absolute", (string?)brush.Attribute("ViewportUnits"));
    }

    [Fact]
    public void The_picture_is_marked_as_a_required_resource_of_its_page()
    {
        var zip = XpsExporter.ToBytes(Report(Page(1), Page(2)));
        var rels = XDocument.Parse(ReadEntry(zip, "Documents/1/Pages/_rels/2.fpage.rels"));
        var rel = Assert.Single(rels.Root!.Elements(Rels + "Relationship"));

        Assert.Equal("http://schemas.microsoft.com/xps/2005/06/required-resource", (string?)rel.Attribute("Type"));
        Assert.Equal("/Resources/Images/2.png", (string?)rel.Attribute("Target"));
    }

    [Fact]
    public void The_picture_is_a_png_with_the_page_size_at_the_chosen_dpi()
    {
        var zip = XpsExporter.ToBytes(Report(Page(1, 96, 48)), dpi: 192);
        var bytes = ReadBytes(zip, "Resources/Images/1.png");

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
        using var image = System.Drawing.Image.FromStream(new MemoryStream(bytes));
        Assert.Equal(192, image.Width);
        Assert.Equal(96, image.Height);
    }

    [Fact]
    public void The_dpi_is_kept_inside_72_to_600()
    {
        var low = ReadBytes(XpsExporter.ToBytes(Report(Page(1, 96, 96)), dpi: 1), "Resources/Images/1.png");
        var high = ReadBytes(XpsExporter.ToBytes(Report(Page(1, 96, 96)), dpi: 5000), "Resources/Images/1.png");

        using var small = System.Drawing.Image.FromStream(new MemoryStream(low));
        using var large = System.Drawing.Image.FromStream(new MemoryStream(high));
        Assert.Equal(72, small.Width);
        Assert.Equal(600, large.Width);
    }

    [Fact]
    public void A_report_without_pages_still_gives_one_blank_page()
    {
        var zip = XpsExporter.ToBytes(new ReportResult { ReportName = "Empty" });

        Assert.Contains("Documents/1/Pages/1.fpage", EntryNames(zip));
        Assert.Contains("Resources/Images/1.png", EntryNames(zip));
    }

    [Fact]
    public void A_page_without_a_size_falls_back_to_a4()
    {
        var zip = XpsExporter.ToBytes(Report(new RenderedPage { Number = 1, Width = 0, Height = 0 }));
        var page = XDocument.Parse(ReadEntry(zip, "Documents/1/Pages/1.fpage")).Root!;

        Assert.Equal("794", (string?)page.Attribute("Width"));
        Assert.Equal("1123", (string?)page.Attribute("Height"));
    }

    [Fact]
    public void Writing_to_a_file_and_to_a_stream_give_the_same_parts()
    {
        var report = Report(Page(1), Page(2));
        var path = Path.Combine(Path.GetTempPath(), $"rs-xps-{Guid.NewGuid():N}.xps");
        try
        {
            XpsExporter.Export(report, path);
            var fromFile = EntryNames(File.ReadAllBytes(path));
            var fromBytes = EntryNames(XpsExporter.ToBytes(report));

            Assert.Equal(fromBytes, fromFile);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void The_stream_is_left_open()
    {
        using var ms = new MemoryStream();
        XpsExporter.Export(Report(Page(1)), ms);

        Assert.True(ms.CanWrite);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void A_missing_report_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => XpsExporter.ToBytes(null!));
    }
}
