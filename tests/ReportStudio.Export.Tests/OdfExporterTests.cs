using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>OpenDocument output (requirement 44b). The page model is built by hand, so no fonts or window are needed.</summary>
public class OdfExporterTests
{
    private static readonly XNamespace NsOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace NsTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace NsText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace NsStyle = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace NsNumber = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    private static readonly XNamespace NsFo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    private static ReportResult One(double width, double height, params RenderedItem[] items)
    {
        var result = new ReportResult { ReportName = "Pay" };
        var page = new RenderedPage { Number = 1, Width = width, Height = height };
        page.Items.AddRange(items);
        result.Pages.Add(page);
        return result;
    }

    private static RenderedItem Item(double x, double y, string text, double width = 100) => new()
    {
        Kind = ItemKind.Text,
        X = x,
        Y = y,
        Width = width,
        Height = 18,
        Text = text
    };

    private static byte[] Odt(ReportResult report)
    {
        using var ms = new MemoryStream();
        OdfExporter.ExportText(report, ms);
        return ms.ToArray();
    }

    private static byte[] Ods(ReportResult report)
    {
        using var ms = new MemoryStream();
        OdfExporter.ExportSpreadsheet(report, ms, CultureInfo.InvariantCulture);
        return ms.ToArray();
    }

    private static string ReadEntry(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static XDocument Content(byte[] zip) => XDocument.Parse(ReadEntry(zip, "content.xml"));

    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_mimetype_entry_comes_first_and_is_stored_without_compression()
    {
        foreach (var (zip, mime) in new[]
        {
            (Odt(One(794, 1123, Item(40, 20, "A"))), "application/vnd.oasis.opendocument.text"),
            (Ods(One(794, 1123, Item(40, 20, "A"))), "application/vnd.oasis.opendocument.spreadsheet")
        })
        {
            Assert.Equal((byte)'P', zip[0]);
            Assert.Equal((byte)'K', zip[1]);
            Assert.Equal(0, zip[8]);                                  // compression method 0 = stored
            Assert.Equal(0, zip[9]);
            Assert.Equal("mimetype", Encoding.ASCII.GetString(zip, 30, 8));
            Assert.Equal(mime, Encoding.ASCII.GetString(zip, 38, mime.Length));   // the type sits right after the name
        }
    }

    [Fact]
    public void The_package_holds_the_four_parts_and_every_one_is_well_formed_xml()
    {
        foreach (var zip in new[] { Odt(One(794, 1123, Item(40, 20, "A"))), Ods(One(794, 1123, Item(40, 20, "A"))) })
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            Assert.Equal("mimetype", archive.Entries[0].FullName);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("META-INF/manifest.xml", names);
            Assert.Contains("content.xml", names);
            Assert.Contains("styles.xml", names);
            Assert.Contains("meta.xml", names);

            foreach (var name in new[] { "META-INF/manifest.xml", "content.xml", "styles.xml", "meta.xml" })
                XDocument.Parse(ReadEntry(zip, name));                // throws when not well-formed
        }
    }

    [Fact]
    public void The_manifest_names_the_document_type()
    {
        var manifest = XDocument.Parse(ReadEntry(Ods(One(794, 1123, Item(40, 20, "A"))), "META-INF/manifest.xml"));

        Assert.Contains("application/vnd.oasis.opendocument.spreadsheet", manifest.ToString());
    }

    [Fact]
    public void Items_on_one_line_become_cells_of_one_table_row()
    {
        var doc = Content(Odt(One(794, 1123, Item(40, 20, "Name"), Item(300, 20, "Amount"), Item(40, 60, "Asha"))));

        var rows = doc.Descendants(NsTable + "table-row").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Elements(NsTable + "table-cell").Count());
        Assert.Equal(2, rows[1].Elements(NsTable + "table-cell").Count());   // the gap is filled with an empty cell
        Assert.Equal(2, doc.Descendants(NsTable + "table-column").Count());
        Assert.Equal("Name", rows[0].Elements(NsTable + "table-cell").First().Value);
        Assert.Equal("Asha", rows[1].Elements(NsTable + "table-cell").First().Value);
    }

    [Fact]
    public void A_wide_item_spans_columns_and_leaves_covered_cells()
    {
        var doc = Content(Odt(One(794, 1123, Item(40, 20, "A"), Item(300, 20, "B"), Item(40, 60, "Wide", width: 300))));

        var second = doc.Descendants(NsTable + "table-row").ElementAt(1);
        var cell = Assert.Single(second.Elements(NsTable + "table-cell"));
        Assert.Equal("2", cell.Attribute(NsTable + "number-columns-spanned")!.Value);
        Assert.Single(second.Elements(NsTable + "covered-table-cell"));
    }

    [Fact]
    public void The_spreadsheet_stores_numbers_and_dates_as_values_and_everything_else_as_text()
    {
        var doc = Content(Ods(One(794, 1123,
            Item(40, 20, "Asha"), Item(300, 20, "1,250.50"),
            Item(40, 60, "15-11-2026"), Item(300, 60, "007"))));

        var rows = doc.Descendants(NsTable + "table-row").ToList();
        var first = rows[0].Elements(NsTable + "table-cell").ToList();
        var second = rows[1].Elements(NsTable + "table-cell").ToList();

        Assert.Equal("string", first[0].Attribute(NsOffice + "value-type")!.Value);
        Assert.Equal("float", first[1].Attribute(NsOffice + "value-type")!.Value);
        Assert.Equal("1250.50", first[1].Attribute(NsOffice + "value")!.Value);
        Assert.Equal("date", second[0].Attribute(NsOffice + "value-type")!.Value);
        Assert.Equal("2026-11-15", second[0].Attribute(NsOffice + "date-value")!.Value);
        Assert.Equal("string", second[1].Attribute(NsOffice + "value-type")!.Value);     // "007" is an identifier, not a number
        Assert.Equal("007", second[1].Value);
    }

    [Fact]
    public void A_number_keeps_its_printed_decimals_and_grouping()
    {
        var doc = Content(Ods(One(794, 1123, Item(40, 20, "1,250.50"), Item(300, 20, "42"))));

        var numberStyles = doc.Descendants(NsNumber + "number").ToList();
        Assert.Contains(numberStyles, n => n.Attribute(NsNumber + "decimal-places")!.Value == "2" && n.Attribute(NsNumber + "grouping")!.Value == "true");
        Assert.Contains(numberStyles, n => n.Attribute(NsNumber + "decimal-places")!.Value == "0" && n.Attribute(NsNumber + "grouping")!.Value == "false");
        Assert.Empty(doc.Descendants(NsNumber + "date-style"));          // no date used, so no date style
    }

    [Fact]
    public void A_text_document_does_not_turn_numbers_into_values()
    {
        var doc = Content(Odt(One(794, 1123, Item(40, 20, "1,250.50"))));

        Assert.DoesNotContain(doc.Descendants(), e => e.Attribute(NsOffice + "value-type") != null);
    }

    [Fact]
    public void Bold_italic_size_colour_alignment_and_background_become_styles()
    {
        var item = Item(40, 20, "Total");
        item.Bold = true;
        item.Italic = true;
        item.FontSize = 12;
        item.ForeColor = "#FF0000";
        item.BackColor = "#00FF00";
        item.HAlign = HAlign.Right;

        var doc = Content(Odt(One(794, 1123, item)));

        var text = doc.Descendants(NsStyle + "text-properties").Single();
        Assert.Equal("bold", text.Attribute(NsFo + "font-weight")!.Value);
        Assert.Equal("italic", text.Attribute(NsFo + "font-style")!.Value);
        Assert.Equal("12pt", text.Attribute(NsFo + "font-size")!.Value);
        Assert.Equal("#ff0000", text.Attribute(NsFo + "color")!.Value);
        Assert.Equal("end", doc.Descendants(NsStyle + "paragraph-properties").Single().Attribute(NsFo + "text-align")!.Value);
        Assert.Equal("#00ff00", doc.Descendants(NsStyle + "table-cell-properties").Single().Attribute(NsFo + "background-color")!.Value);
    }

    [Fact]
    public void Cells_with_the_same_look_share_one_style()
    {
        var doc = Content(Odt(One(794, 1123, Item(40, 20, "A"), Item(300, 20, "B"), Item(40, 60, "C"))));

        Assert.Single(doc.Descendants(NsStyle + "text-properties"));
        Assert.Single(doc.Descendants(NsStyle + "table-cell-properties"));
    }

    [Fact]
    public void Markup_characters_in_the_text_survive_a_round_trip()
    {
        var doc = Content(Odt(One(794, 1123, Item(40, 20, "a & <b> \"c\" नमस्ते"))));

        Assert.Equal("a & <b> \"c\" नमस्ते", doc.Descendants(NsText + "span").Single().Value);
    }

    [Fact]
    public void Spaces_tabs_and_line_breaks_are_written_the_way_opendocument_keeps_them()
    {
        Assert.Equal("a <text:s text:c=\"1\"/>b", OdfExporter.Inline("a  b"));
        Assert.Equal("<text:s text:c=\"1\"/>x", OdfExporter.Inline(" x"));
        Assert.Equal("x <text:s text:c=\"2\"/>", OdfExporter.Inline("x   "));
        Assert.Equal("a<text:tab/>b", OdfExporter.Inline("a\tb"));
        Assert.Equal("a<text:line-break/>b", OdfExporter.Inline("a\r\nb"));
        Assert.Equal("ab", OdfExporter.Inline("a\u0001b"));            // a control character XML forbids is dropped
        Assert.Equal("&amp;&lt;&gt;", OdfExporter.Inline("&<>"));
    }

    [Fact]
    public void Attribute_text_is_escaped_too()
    {
        Assert.Equal("a &amp; &lt;b&gt; &quot;c&quot;", OdfExporter.Esc("a & <b> \"c\""));
    }

    [Fact]
    public void Page_size_and_orientation_are_written_to_the_styles()
    {
        var portrait = XDocument.Parse(ReadEntry(Odt(One(794, 1123, Item(40, 20, "A"))), "styles.xml"));
        var landscape = XDocument.Parse(ReadEntry(Odt(One(1123, 794, Item(40, 20, "A"))), "styles.xml"));

        var p = portrait.Descendants(NsStyle + "page-layout-properties").Single();
        var l = landscape.Descendants(NsStyle + "page-layout-properties").Single();
        Assert.Equal("8.2708in", p.Attribute(NsFo + "page-width")!.Value);       // 794 px at 96 per inch
        Assert.Equal("11.6979in", p.Attribute(NsFo + "page-height")!.Value);
        Assert.Equal("portrait", p.Attribute(NsStyle + "print-orientation")!.Value);
        Assert.Equal("landscape", l.Attribute(NsStyle + "print-orientation")!.Value);
    }

    [Fact]
    public void The_report_name_is_the_document_title()
    {
        var meta = ReadEntry(Odt(One(794, 1123, Item(40, 20, "A"))), "meta.xml");

        Assert.Contains("<dc:title>Pay</dc:title>", meta);
    }

    [Fact]
    public void A_report_without_items_still_gives_valid_files()
    {
        var text = Content(Odt(new ReportResult()));
        var sheet = Content(Ods(new ReportResult()));

        Assert.Empty(text.Descendants(NsTable + "table"));                       // a text document needs no table
        Assert.Single(sheet.Descendants(NsTable + "table"));                     // a spreadsheet needs its sheet
    }

    [Fact]
    public void The_stream_overloads_leave_the_stream_open()
    {
        using var ms = new MemoryStream();

        OdfExporter.ExportText(One(794, 1123, Item(40, 20, "A")), ms);

        Assert.True(ms.CanWrite);
        Assert.True(ms.Length > 0);
    }
}