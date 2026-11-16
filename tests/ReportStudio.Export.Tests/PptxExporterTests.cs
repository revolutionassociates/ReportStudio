using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Text;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>PowerPoint output (requirement 44c). The page model is built by hand, so no fonts or window are needed.</summary>
public class PptxExporterTests
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    // A 1 x 1 pixel PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static RenderedPage Page(double width, double height, params RenderedItem[] items)
    {
        var page = new RenderedPage { Number = 1, Width = width, Height = height };
        page.Items.AddRange(items);
        return page;
    }

    private static ReportResult Report(params RenderedPage[] pages)
    {
        var result = new ReportResult { ReportName = "Pay" };
        result.Pages.AddRange(pages);
        return result;
    }

    private static ReportResult One(params RenderedItem[] items) => Report(Page(794, 1123, items));

    private static RenderedItem Text(string text, double x = 10, double y = 20, double w = 200, double h = 30) => new()
    {
        Kind = ItemKind.Text, X = x, Y = y, Width = w, Height = h, Text = text, FontSize = 10, FontFamily = "Arial"
    };

    private static string ReadEntry(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static XDocument Slide(byte[] zip, int number = 1) => XDocument.Parse(ReadEntry(zip, $"ppt/slides/slide{number}.xml"));

    private static List<string> EntryNames(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static IEnumerable<XElement> Shapes(XDocument slide) => slide.Descendants(P + "sp");

    private static long Emu(double px) => (long)Math.Round(px * 9525);

    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_package_has_every_required_part_and_all_of_it_is_well_formed_xml()
    {
        var zip = PptxExporter.ToBytes(One(Text("A")));
        var names = EntryNames(zip);

        foreach (var part in new[]
        {
            "[Content_Types].xml", "_rels/.rels", "docProps/core.xml", "docProps/app.xml", "ppt/presentation.xml",
            "ppt/_rels/presentation.xml.rels", "ppt/slideMasters/slideMaster1.xml", "ppt/slideMasters/_rels/slideMaster1.xml.rels",
            "ppt/slideLayouts/slideLayout1.xml", "ppt/slideLayouts/_rels/slideLayout1.xml.rels", "ppt/theme/theme1.xml",
            "ppt/slides/slide1.xml", "ppt/slides/_rels/slide1.xml.rels"
        })
        {
            Assert.Contains(part, names);
            XDocument.Parse(ReadEntry(zip, part));                    // throws when not well-formed
        }
    }

    [Fact]
    public void Every_part_has_a_content_type_and_every_relationship_points_at_a_part_that_exists()
    {
        var zip = PptxExporter.ToBytes(One(Text("A"), new RenderedItem { Kind = ItemKind.Image, X = 0, Y = 0, Width = 50, Height = 50, Image = Png }));
        var names = EntryNames(zip);

        var types = XDocument.Parse(ReadEntry(zip, "[Content_Types].xml"));
        var overrides = types.Descendants(Types + "Override").Select(o => o.Attribute("PartName")!.Value.TrimStart('/')).ToHashSet();
        var defaults = types.Descendants(Types + "Default").Select(d => d.Attribute("Extension")!.Value).ToHashSet();
        foreach (var name in names.Where(n => n != "[Content_Types].xml"))
            Assert.True(overrides.Contains(name) || defaults.Contains(Path.GetExtension(name).TrimStart('.')), name);

        foreach (var relsPart in names.Where(n => n.EndsWith(".rels", StringComparison.Ordinal)))
        {
            string baseDirectory = relsPart.Contains("_rels/") ? relsPart[..relsPart.IndexOf("_rels/", StringComparison.Ordinal)] : "";
            foreach (var rel in XDocument.Parse(ReadEntry(zip, relsPart)).Descendants(Rels + "Relationship"))
            {
                if (rel.Attribute("TargetMode")?.Value == "External") continue;
                string target = rel.Attribute("Target")!.Value;
                string full = new Uri(new Uri("http://x/" + baseDirectory), target).AbsolutePath.TrimStart('/');
                Assert.Contains(full, names);
            }
        }
    }

    [Fact]
    public void The_slide_size_is_the_first_pages_size_in_emu()
    {
        var presentation = XDocument.Parse(ReadEntry(PptxExporter.ToBytes(One(Text("A"))), "ppt/presentation.xml"));

        var size = presentation.Descendants(P + "sldSz").Single();
        Assert.Equal(Emu(794).ToString(), size.Attribute("cx")!.Value);
        Assert.Equal(Emu(1123).ToString(), size.Attribute("cy")!.Value);
    }

    [Fact]
    public void Every_page_becomes_one_slide()
    {
        var zip = PptxExporter.ToBytes(Report(Page(794, 1123, Text("One")), Page(794, 1123, Text("Two")), Page(794, 1123, Text("Three"))));

        var presentation = XDocument.Parse(ReadEntry(zip, "ppt/presentation.xml"));
        Assert.Equal(3, presentation.Descendants(P + "sldId").Count());
        Assert.Equal("Two", Slide(zip, 2).Descendants(A + "t").Single().Value);
        Assert.Equal("3", XDocument.Parse(ReadEntry(zip, "docProps/app.xml")).Root!.Elements().Single(e => e.Name.LocalName == "Slides").Value);
    }

    [Fact]
    public void A_report_without_pages_gives_one_empty_slide()
    {
        var zip = PptxExporter.ToBytes(new ReportResult());

        Assert.Empty(Shapes(Slide(zip)));
        Assert.Single(XDocument.Parse(ReadEntry(zip, "ppt/presentation.xml")).Descendants(P + "sldId"));
    }

    [Fact]
    public void Text_is_a_text_box_with_its_size_font_and_colour()
    {
        var item = Text("Total");
        item.Bold = true;
        item.Italic = true;
        item.Underline = true;
        item.FontSize = 12;
        item.FontFamily = "Georgia";
        item.ForeColor = "#FF0000";
        item.HAlign = HAlign.Right;
        item.VAlign = VAlign.Middle;

        var shape = Shapes(Slide(PptxExporter.ToBytes(One(item)))).Single();

        Assert.Equal("1", shape.Descendants(P + "cNvSpPr").Single().Attribute("txBox")!.Value);
        var xfrm = shape.Descendants(A + "xfrm").Single();
        Assert.Equal(Emu(10).ToString(), xfrm.Element(A + "off")!.Attribute("x")!.Value);
        Assert.Equal(Emu(200).ToString(), xfrm.Element(A + "ext")!.Attribute("cx")!.Value);
        Assert.Equal("r", shape.Descendants(A + "pPr").Single().Attribute("algn")!.Value);
        Assert.Equal("ctr", shape.Descendants(A + "bodyPr").Single().Attribute("anchor")!.Value);

        var run = shape.Descendants(A + "rPr").Single();
        Assert.Equal("1200", run.Attribute("sz")!.Value);
        Assert.Equal("1", run.Attribute("b")!.Value);
        Assert.Equal("1", run.Attribute("i")!.Value);
        Assert.Equal("sng", run.Attribute("u")!.Value);
        Assert.Equal("FF0000", run.Descendants(A + "srgbClr").First().Attribute("val")!.Value);
        Assert.Equal("Georgia", run.Element(A + "latin")!.Attribute("typeface")!.Value);
        Assert.Equal("Total", shape.Descendants(A + "t").Single().Value);
    }

    [Fact]
    public void Rich_text_keeps_the_look_of_each_run()
    {
        var item = Text("Hello world");
        item.Runs = new List<TextRun> { new("Hello ", Bold: true), new("world", Strike: true, Color: "#00FF00") };

        var runs = Shapes(Slide(PptxExporter.ToBytes(One(item)))).Single().Descendants(A + "r").ToList();

        Assert.Equal(2, runs.Count);
        Assert.Equal("1", runs[0].Element(A + "rPr")!.Attribute("b")!.Value);
        Assert.Null(runs[1].Element(A + "rPr")!.Attribute("b"));
        Assert.Equal("sngStrike", runs[1].Element(A + "rPr")!.Attribute("strike")!.Value);
        Assert.Equal("00FF00", runs[1].Descendants(A + "srgbClr").First().Attribute("val")!.Value);
    }

    [Fact]
    public void A_line_break_starts_a_new_paragraph_and_markup_characters_survive()
    {
        var item = Text("a & <b>\r\nsecond");

        var shape = Shapes(Slide(PptxExporter.ToBytes(One(item)))).Single();

        var paragraphs = shape.Descendants(A + "p").ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("a & <b>", paragraphs[0].Descendants(A + "t").Single().Value);
        Assert.Equal("second", paragraphs[1].Descendants(A + "t").Single().Value);
    }

    [Fact]
    public void A_text_with_nothing_to_show_makes_no_shape()
    {
        Assert.Empty(Shapes(Slide(PptxExporter.ToBytes(One(Text(""))))));
    }

    [Fact]
    public void A_box_with_a_fill_and_a_border_keeps_both()
    {
        var item = Text("Cell");
        item.BackColor = "#EEEEEE";
        item.BorderColor = "#112233";
        item.BorderWidth = 2;

        var props = Shapes(Slide(PptxExporter.ToBytes(One(item)))).Single().Element(P + "spPr")!;

        Assert.Equal("EEEEEE", props.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val")!.Value);
        var line = props.Element(A + "ln")!;
        Assert.Equal(Emu(2).ToString(), line.Attribute("w")!.Value);
        Assert.Equal("112233", line.Descendants(A + "srgbClr").Single().Attribute("val")!.Value);
    }

    [Fact]
    public void Rectangles_ellipses_and_rounded_corners_become_the_matching_shapes()
    {
        var plain = new RenderedItem { Kind = ItemKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 50, BackColor = "#FF0000" };
        var round = new RenderedItem { Kind = ItemKind.Rectangle, X = 0, Y = 60, Width = 100, Height = 50, BackColor = "#FF0000", CornerRadius = 10 };
        var oval = new RenderedItem { Kind = ItemKind.Ellipse, X = 0, Y = 120, Width = 100, Height = 50, BorderColor = "#000000", BorderWidth = 1 };

        var shapes = Shapes(Slide(PptxExporter.ToBytes(One(plain, round, oval)))).ToList();

        Assert.Equal("rect", shapes[0].Descendants(A + "prstGeom").Single().Attribute("prst")!.Value);
        var rounded = shapes[1].Descendants(A + "prstGeom").Single();
        Assert.Equal("roundRect", rounded.Attribute("prst")!.Value);
        Assert.Equal("val 20000", rounded.Descendants(A + "gd").Single().Attribute("fmla")!.Value);   // 10 px of a 50 px side
        Assert.Equal("ellipse", shapes[2].Descendants(A + "prstGeom").Single().Attribute("prst")!.Value);
        Assert.NotNull(shapes[2].Element(P + "spPr")!.Element(A + "noFill"));
    }

    [Fact]
    public void A_gradient_fill_runs_in_the_chosen_direction()
    {
        var across = new RenderedItem
        {
            Kind = ItemKind.Rectangle, Width = 100, Height = 50, BackColor = "#FF0000", GradientColor = "#0000FF",
            GradientDirection = GradientDirection.LeftToRight
        };
        var down = new RenderedItem
        {
            Kind = ItemKind.Rectangle, Width = 100, Height = 50, BackColor = "#FF0000", GradientColor = "#0000FF",
            GradientDirection = GradientDirection.TopToBottom
        };

        var shapes = Shapes(Slide(PptxExporter.ToBytes(One(across, down)))).ToList();

        Assert.Equal("0", shapes[0].Descendants(A + "lin").Single().Attribute("ang")!.Value);
        Assert.Equal("5400000", shapes[1].Descendants(A + "lin").Single().Attribute("ang")!.Value);
        Assert.Equal(2, shapes[0].Descendants(A + "gs").Count());
    }

    [Fact]
    public void A_line_becomes_a_connector_with_its_colour_and_width()
    {
        var line = new RenderedItem { Kind = ItemKind.Line, X = 10, Y = 10, Width = 100, Height = 0, ForeColor = "#FF8800", BorderWidth = 3 };

        var connector = Slide(PptxExporter.ToBytes(One(line))).Descendants(P + "cxnSp").Single();

        Assert.Equal("line", connector.Descendants(A + "prstGeom").Single().Attribute("prst")!.Value);
        Assert.Equal(Emu(3).ToString(), connector.Descendants(A + "ln").Single().Attribute("w")!.Value);
        Assert.Equal("FF8800", connector.Descendants(A + "srgbClr").Single().Attribute("val")!.Value);
    }

    [Fact]
    public void A_picture_is_embedded_once_however_often_it_is_used()
    {
        var one = new RenderedItem { Kind = ItemKind.Image, X = 0, Y = 0, Width = 50, Height = 50, Image = Png };
        var two = new RenderedItem { Kind = ItemKind.Image, X = 60, Y = 0, Width = 50, Height = 50, Image = Png };
        var zip = PptxExporter.ToBytes(Report(Page(794, 1123, one, two), Page(794, 1123, one)));

        Assert.Single(EntryNames(zip), n => n.StartsWith("ppt/media/", StringComparison.Ordinal));
        Assert.Equal(2, Slide(zip, 1).Descendants(P + "pic").Count());
        Assert.Single(Slide(zip, 2).Descendants(P + "pic"));

        string embedId = Slide(zip, 1).Descendants(A + "blip").First().Attribute(R + "embed")!.Value;
        var rel = XDocument.Parse(ReadEntry(zip, "ppt/slides/_rels/slide1.xml.rels")).Descendants(Rels + "Relationship")
            .Single(r => r.Attribute("Id")!.Value == embedId);
        Assert.Equal("../media/image1.png", rel.Attribute("Target")!.Value);
    }

    [Fact]
    public void A_picture_keeps_its_proportions_and_sits_in_the_middle_of_its_box()
    {
        var item = new RenderedItem { Kind = ItemKind.Image, X = 10, Y = 20, Width = 200, Height = 100, Image = Png };   // a square picture

        var xfrm = Slide(PptxExporter.ToBytes(One(item))).Descendants(P + "pic").Single().Descendants(A + "xfrm").Single();

        Assert.Equal(Emu(60).ToString(), xfrm.Element(A + "off")!.Attribute("x")!.Value);     // 10 + (200 - 100) / 2
        Assert.Equal(Emu(20).ToString(), xfrm.Element(A + "off")!.Attribute("y")!.Value);
        Assert.Equal(Emu(100).ToString(), xfrm.Element(A + "ext")!.Attribute("cx")!.Value);
        Assert.Equal(Emu(100).ToString(), xfrm.Element(A + "ext")!.Attribute("cy")!.Value);
    }

    [Fact]
    public void An_unusable_or_unsafe_picture_is_left_out_but_its_box_stays()
    {
        var garbage = new RenderedItem { Kind = ItemKind.Image, Width = 50, Height = 50, BackColor = "#CCCCCC", Image = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 } };
        var script = new RenderedItem
        {
            Kind = ItemKind.Image, Width = 50, Height = 50,
            Image = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")
        };

        var slide = Slide(PptxExporter.ToBytes(One(garbage, script)));

        Assert.Empty(slide.Descendants(P + "pic"));
        Assert.Single(Shapes(slide));                                                          // the grey box of the first
    }

    [Fact]
    public void A_qr_code_is_one_drawn_shape_made_of_its_modules()
    {
        var qr = new RenderedItem { Kind = ItemKind.QrCode, X = 10, Y = 10, Width = 100, Height = 100, Text = "HELLO", ForeColor = "#000000" };

        var shape = Shapes(Slide(PptxExporter.ToBytes(One(qr)))).Single();

        Assert.Single(shape.Descendants(A + "custGeom"));
        Assert.True(shape.Descendants(A + "moveTo").Count() > 10);
        Assert.Equal(shape.Descendants(A + "moveTo").Count(), shape.Descendants(A + "close").Count());
    }

    [Fact]
    public void A_web_link_becomes_a_link_on_the_shape_and_an_unsafe_one_is_dropped()
    {
        var safe = Text("Site");
        safe.Link = "https://example.com/page";
        var unsafeLink = Text("Bad", y: 80);
        unsafeLink.Link = "javascript:alert(1)";
        var zip = PptxExporter.ToBytes(One(safe, unsafeLink));

        var slide = Slide(zip);
        var link = Assert.Single(slide.Descendants(A + "hlinkClick"));
        var rel = XDocument.Parse(ReadEntry(zip, "ppt/slides/_rels/slide1.xml.rels")).Descendants(Rels + "Relationship")
            .Single(r => r.Attribute("Id")!.Value == link.Attribute(R + "id")!.Value);
        Assert.Equal("https://example.com/page", rel.Attribute("Target")!.Value);
        Assert.Equal("External", rel.Attribute("TargetMode")!.Value);
        Assert.EndsWith("/hyperlink", rel.Attribute("Type")!.Value);
    }

    [Fact]
    public void The_page_background_becomes_the_slide_background()
    {
        var page = Page(794, 1123, Text("A"));
        page.Background = new RenderedBackground { Color = "#FFFF00" };

        var background = Slide(PptxExporter.ToBytes(Report(page))).Descendants(P + "bg").Single();

        Assert.Equal("FFFF00", background.Descendants(A + "srgbClr").Single().Attribute("val")!.Value);
    }

    [Fact]
    public void A_text_watermark_is_a_faded_turned_box_behind_everything()
    {
        var page = Page(794, 1123, Text("Body"));
        page.Watermark = new RenderedWatermark { Text = "DRAFT", Opacity = 0.3, Angle = 45, FontSize = 72, Color = "#9CA3AF" };

        var shapes = Shapes(Slide(PptxExporter.ToBytes(Report(page)))).ToList();

        Assert.Equal(2, shapes.Count);
        Assert.Equal("DRAFT", shapes[0].Descendants(A + "t").Single().Value);                    // first = behind
        Assert.Equal("Body", shapes[1].Descendants(A + "t").Single().Value);
        Assert.Equal("30000", shapes[0].Descendants(A + "alpha").Single().Attribute("val")!.Value);
        Assert.Equal("18900000", shapes[0].Descendants(A + "xfrm").Single().Attribute("rot")!.Value);   // 45 degrees counter-clockwise = 315 clockwise
    }

    [Fact]
    public void Turned_text_keeps_its_fill_upright_and_turns_only_the_text()
    {
        var item = Text("Up");
        item.BackColor = "#EEEEEE";
        item.TextRotation = 90;

        var shapes = Shapes(Slide(PptxExporter.ToBytes(One(item)))).ToList();

        Assert.Equal(2, shapes.Count);
        Assert.Null(shapes[0].Descendants(A + "xfrm").Single().Attribute("rot"));                // the box behind
        Assert.Equal("16200000", shapes[1].Descendants(A + "xfrm").Single().Attribute("rot")!.Value);   // 90 counter-clockwise = 270 clockwise
        Assert.Equal("Up", shapes[1].Descendants(A + "t").Single().Value);
    }

    [Fact]
    public void A_page_of_another_size_is_scaled_to_fit_and_centred()
    {
        var wide = Page(1123, 794, new RenderedItem { Kind = ItemKind.Rectangle, X = 0, Y = 0, Width = 1123, Height = 100, BackColor = "#FF0000" });
        var zip = PptxExporter.ToBytes(Report(Page(794, 1123, Text("A")), wide));

        var xfrm = Shapes(Slide(zip, 2)).Single().Descendants(A + "xfrm").Single();

        long deckWidth = Emu(794);
        Assert.InRange(long.Parse(xfrm.Element(A + "ext")!.Attribute("cx")!.Value), deckWidth - 2, deckWidth + 2);     // the full width of the slide
        Assert.Equal("0", xfrm.Element(A + "off")!.Attribute("x")!.Value);
        Assert.True(long.Parse(xfrm.Element(A + "off")!.Attribute("y")!.Value) > 0);                                  // centred, so lower than the top
    }

    [Fact]
    public void The_report_name_is_the_document_title()
    {
        var core = ReadEntry(PptxExporter.ToBytes(One(Text("A"))), "docProps/core.xml");

        Assert.Contains("<dc:title>Pay</dc:title>", core);
    }

    [Fact]
    public void The_stream_overload_leaves_the_stream_open_and_matches_the_byte_output_in_size()
    {
        var report = One(Text("A"));
        using var ms = new MemoryStream();

        PptxExporter.Export(report, ms);

        Assert.True(ms.CanWrite);
        Assert.Equal(PptxExporter.ToBytes(report).Length, ms.Length);
    }

    [Fact]
    public void Characters_xml_forbids_are_dropped_from_text()
    {
        Assert.Equal("ab", PptxExporter.Esc("a\u0001b"));
        Assert.Equal("&amp;&lt;&gt;&quot;", PptxExporter.Esc("&<>\""));
    }
}
