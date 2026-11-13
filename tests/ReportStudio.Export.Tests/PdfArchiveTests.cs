using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using ReportStudio.Export.Pdf;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>PDF/A-2b archive copies and tagged PDF (requirement 43a).</summary>
public class PdfArchiveTests
{
    private static byte[] Make(PdfOptions? options)
    {
        var def = new ReportDefinition { Name = "Archive" };
        def.DataSource.Provider = DbProvider.Host;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Name}", Width = 200, Height = 20 });

        var rows = new[] { new Dictionary<string, object?> { ["Name"] = "Asha" } };
        return ReportRunner.ToPdf(def, rows, pdf: options);
    }

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    /// <summary>The page content streams, inflated (a page's content starts with the scaling "q" the writer puts first).</summary>
    private static List<string> PageContents(byte[] pdf)
    {
        var text = Text(pdf);
        var found = new List<string>();
        int at = 0;
        while ((at = text.IndexOf("\nstream\n", at, StringComparison.Ordinal)) >= 0)
        {
            int start = at + 8;
            int end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                var content = Encoding.Latin1.GetString(output.ToArray());
                if (content.StartsWith("q\n", StringComparison.Ordinal)) found.Add(content);
            }
            catch (InvalidDataException)
            {
                // a stream that is not Flate data (the XMP packet, say)
            }
            at = end;
        }
        return found;
    }

    private static int PageCount(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [Fact]
    public void An_ordinary_pdf_has_neither_archive_parts_nor_tags()
    {
        var pdf = Make(null);
        var text = Text(pdf);

        Assert.StartsWith("%PDF-1.6", text);
        Assert.DoesNotContain("pdfaid", text);
        Assert.DoesNotContain("/StructTreeRoot", text);
        Assert.DoesNotContain("/OutputIntents", text);
        Assert.All(PageContents(pdf), c => Assert.DoesNotContain("BDC", c));
    }

    [Fact]
    public void A_pdfa_file_declares_its_conformance_and_its_colours()
    {
        var pdf = Make(new PdfOptions { Conformance = PdfConformance.PdfA2b });
        var text = Text(pdf);

        Assert.StartsWith("%PDF-1.7", text);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", text);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", text);
        Assert.Contains("/Type /Metadata", text);
        Assert.Contains("/OutputIntents", text);
        Assert.Contains("/GTS_PDFA1", text);
        Assert.Contains("/DestOutputProfile", text);
        Assert.Contains("/FontFile2", text);                  // every font is embedded
        Assert.DoesNotContain("/Encrypt", text);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void The_dates_in_the_info_dictionary_and_the_metadata_agree()
    {
        var when = new DateTime(2026, 11, 13, 7, 24, 0, DateTimeKind.Utc);
        var pdf = Make(new PdfOptions { Conformance = PdfConformance.PdfA2b, CreationTime = when });
        var text = Text(pdf);

        Assert.Contains("<xmp:CreateDate>2026-11-13T07:24:00Z</xmp:CreateDate>", text);
        Assert.Contains("<xmp:ModifyDate>2026-11-13T07:24:00Z</xmp:ModifyDate>", text);
        // Info strings are written as hexadecimal text
        Assert.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("D:20261113072400Z")), text);
    }

    [Fact]
    public void The_title_is_in_the_metadata()
    {
        var text = Text(Make(new PdfOptions { Conformance = PdfConformance.PdfA2b }));

        Assert.Matches(new Regex("<rdf:li xml:lang=\"x-default\">[^<]+</rdf:li>"), text);
        Assert.Contains("<pdf:Producer>ReportStudio</pdf:Producer>", text);
    }

    [Fact]
    public void A_pdfa_file_cannot_be_encrypted()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Make(new PdfOptions { Conformance = PdfConformance.PdfA2b, UserPassword = "E1-2026" }));
        Assert.Throws<InvalidOperationException>(() =>
            Make(new PdfOptions { Conformance = PdfConformance.PdfA2b, AllowCopy = false }));
    }

    [Fact]
    public void A_tagged_pdf_has_a_structure_tree_and_marks_its_text()
    {
        var pdf = Make(new PdfOptions { Tagged = true });
        var text = Text(pdf);

        Assert.Contains("/StructTreeRoot", text);
        Assert.Contains("/MarkInfo", text);
        Assert.Contains("/ParentTree", text);
        Assert.Contains("/StructParents 0", text);
        Assert.Contains("/S /Document", text);
        Assert.Contains("/S /P", text);

        var content = Assert.Single(PageContents(pdf));
        Assert.Contains("/P <</MCID 0>> BDC", content);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void Everything_that_is_not_text_is_marked_as_decoration_and_every_mark_is_closed()
    {
        var pdf = Make(new PdfOptions { Tagged = true });
        var content = Assert.Single(PageContents(pdf));

        int opened = Regex.Matches(content, @"\bBDC\b").Count + Regex.Matches(content, @"\bBMC\b").Count;
        int closed = Regex.Matches(content, @"\bEMC\b").Count;
        Assert.True(opened >= 2);                              // the page decoration and the text
        Assert.Equal(opened, closed);
        Assert.Contains("/Artifact BMC", content);
    }

    [Fact]
    public void The_language_of_a_tagged_pdf_can_be_set()
    {
        var text = Text(Make(new PdfOptions { Tagged = true, Language = "en-IN" }));

        Assert.Contains("/Lang <" + Convert.ToHexString(Encoding.ASCII.GetBytes("en-IN")) + ">", text);
    }

    [Fact]
    public void Pdfa_and_tagged_can_be_combined()
    {
        var pdf = Make(new PdfOptions { Conformance = PdfConformance.PdfA2b, Tagged = true });
        var text = Text(pdf);

        Assert.Contains("/GTS_PDFA1", text);
        Assert.Contains("/StructTreeRoot", text);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void The_srgb_profile_is_a_well_formed_icc_profile()
    {
        var icc = PdfIccProfile.SRgb();

        Assert.Equal((uint)icc.Length, BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(0, 4)));
        Assert.Equal("mntr", Encoding.ASCII.GetString(icc, 12, 4));
        Assert.Equal("RGB ", Encoding.ASCII.GetString(icc, 16, 4));
        Assert.Equal("XYZ ", Encoding.ASCII.GetString(icc, 20, 4));
        Assert.Equal("acsp", Encoding.ASCII.GetString(icc, 36, 4));
        Assert.Equal(2, icc[8]);                               // version 2.x

        uint tags = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128, 4));
        Assert.Equal(9u, tags);
        var names = new List<string>();
        for (int i = 0; i < tags; i++)
        {
            int entry = 132 + 12 * i;
            names.Add(Encoding.ASCII.GetString(icc, entry, 4));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 4, 4));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 8, 4));
            Assert.Equal(0u, offset % 4);                      // tag data starts on a 4-byte boundary
            Assert.True(offset + size <= icc.Length);
        }
        Assert.Equal(new[] { "desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "cprt" }, names);
    }
}
