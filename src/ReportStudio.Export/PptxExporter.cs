using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Writes a PowerPoint presentation (.pptx): one slide for each report page, with every element as a native PowerPoint object so
/// it can be edited: text in text boxes (rich text keeps bold, italic, underline, strike and colour), rectangles, ellipses and
/// lines as shapes (rounded corners, gradients, borders), pictures as pictures, and barcodes and QR codes as one drawn shape.
/// The page background, the watermark and web, e-mail and phone links are kept. The slide size is the first page's size; a page of
/// another size is scaled to fit and centred.
/// </summary>
public static class PptxExporter
{
    private const double EmuPerPx = 9525;            // 1 px at 96 dpi
    private const long MinSlide = 914400;            // PowerPoint accepts 1 to 56 inches
    private const long MaxSlide = 51206400;

    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Rect = "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>";
    private const string NoFill = "<a:noFill/>";
    private const string NoLine = "<a:ln><a:noFill/></a:ln>";

    public static void Export(ReportResult report, string path)
    {
        using var file = File.Create(path);
        Export(report, file);
    }

    public static byte[] ToBytes(ReportResult report)
    {
        using var ms = new MemoryStream();
        Export(report, ms);
        return ms.ToArray();
    }

    /// <summary>Writes the presentation to a stream (left open).</summary>
    public static void Export(ReportResult report, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(report);

        var first = report.Pages.FirstOrDefault();
        double deckW = first is { Width: > 0 } ? first.Width : 794;
        double deckH = first is { Height: > 0 } ? first.Height : 1123;
        long cx = Math.Clamp((long)Math.Round(deckW * EmuPerPx), MinSlide, MaxSlide);
        long cy = Math.Clamp((long)Math.Round(deckH * EmuPerPx), MinSlide, MaxSlide);

        var media = new Media();
        var slides = new List<(string Xml, string Rels)>();
        foreach (var page in report.Pages)
            slides.Add(new Slide(media, deckW, deckH, page).Build());
        if (slides.Count == 0)
            slides.Add(new Slide(media, deckW, deckH, new RenderedPage { Number = 1, Width = deckW, Height = deckH }).Build());

        // Built in memory first, then copied: the zip writer needs a seekable stream.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes(slides.Count, media));
            Add(zip, "_rels/.rels", RootRels());
            Add(zip, "docProps/core.xml", CoreProperties(report.ReportName));
            Add(zip, "docProps/app.xml", AppProperties(slides.Count));
            Add(zip, "ppt/presentation.xml", Presentation(slides.Count, cx, cy));
            Add(zip, "ppt/_rels/presentation.xml.rels", PresentationRels(slides.Count));
            Add(zip, "ppt/slideMasters/slideMaster1.xml", SlideMaster());
            Add(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", MasterRels());
            Add(zip, "ppt/slideLayouts/slideLayout1.xml", SlideLayout());
            Add(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", LayoutRels());
            Add(zip, "ppt/theme/theme1.xml", Theme());
            for (int i = 0; i < slides.Count; i++)
            {
                Add(zip, $"ppt/slides/slide{i + 1}.xml", slides[i].Xml);
                Add(zip, $"ppt/slides/_rels/slide{i + 1}.xml.rels", slides[i].Rels);
            }
            foreach (var (name, data) in media.Files)
            {
                var entry = zip.CreateEntry("ppt/media/" + name, CompressionLevel.Fastest);
                using var s = entry.Open();
                s.Write(data, 0, data.Length);
            }
        }
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    // ---- the fixed parts of the package ------------------------------------------------------

    private static string ContentTypes(int slideCount, Media media)
    {
        var sb = new StringBuilder(Declaration);
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        foreach (var ext in media.Extensions.OrderBy(e => e, StringComparer.Ordinal))
            sb.Append("<Default Extension=\"").Append(ext).Append("\" ContentType=\"").Append(Media.ContentType(ext)).Append("\"/>");
        sb.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>");
        sb.Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>");
        sb.Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>");
        sb.Append("<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        for (int i = 1; i <= slideCount; i++)
            sb.Append("<Override PartName=\"/ppt/slides/slide").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        sb.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
        sb.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string RootRels() =>
        Declaration + "<Relationships xmlns=\"" + RelNs + "\">" +
        "<Relationship Id=\"rId1\" Type=\"" + RelBase + "officeDocument\" Target=\"ppt/presentation.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"" + RelBase + "extended-properties\" Target=\"docProps/app.xml\"/>" +
        "</Relationships>";

    private static string CoreProperties(string? title) =>
        Declaration +
        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
        " xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        (string.IsNullOrWhiteSpace(title) ? "" : "<dc:title>" + Esc(title) + "</dc:title>") +
        "<dc:creator>ReportStudio</dc:creator></cp:coreProperties>";

    private static string AppProperties(int slides) =>
        Declaration +
        "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
        "<Application>ReportStudio</Application><Slides>" + slides.ToString(CultureInfo.InvariantCulture) + "</Slides></Properties>";

    private static string Presentation(int slideCount, long cx, long cy)
    {
        var sb = new StringBuilder(Declaration);
        sb.Append("<p:presentation xmlns:a=\"").Append(NsA).Append("\" xmlns:r=\"").Append(NsR).Append("\" xmlns:p=\"").Append(NsP).Append("\" saveSubsetFonts=\"1\">");
        sb.Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>");
        for (int i = 0; i < slideCount; i++)
            sb.Append("<p:sldId id=\"").Append(256 + i).Append("\" r:id=\"rId").Append(i + 2).Append("\"/>");
        sb.Append("</p:sldIdLst><p:sldSz cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
        return sb.ToString();
    }

    private static string PresentationRels(int slideCount)
    {
        var sb = new StringBuilder(Declaration);
        sb.Append("<Relationships xmlns=\"").Append(RelNs).Append("\">");
        sb.Append("<Relationship Id=\"rId1\" Type=\"").Append(RelBase).Append("slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>");
        for (int i = 0; i < slideCount; i++)
            sb.Append("<Relationship Id=\"rId").Append(i + 2).Append("\" Type=\"").Append(RelBase).Append("slide\" Target=\"slides/slide").Append(i + 1).Append(".xml\"/>");
        sb.Append("<Relationship Id=\"rId").Append(slideCount + 2).Append("\" Type=\"").Append(RelBase).Append("theme\" Target=\"theme/theme1.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private const string EmptyTree =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string SlideMaster() =>
        Declaration + "<p:sldMaster xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsR + "\" xmlns:p=\"" + NsP + "\">" +
        "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" + EmptyTree + "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\"" +
        " accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
        "<p:txStyles>" +
        "<p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"4400\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle><a:lvl1pPr marL=\"0\" algn=\"l\"><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:bodyStyle>" +
        "<p:otherStyle><a:lvl1pPr marL=\"0\" algn=\"l\"><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:otherStyle>" +
        "</p:txStyles></p:sldMaster>";

    private static string MasterRels() =>
        Declaration + "<Relationships xmlns=\"" + RelNs + "\">" +
        "<Relationship Id=\"rId1\" Type=\"" + RelBase + "slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"" + RelBase + "theme\" Target=\"../theme/theme1.xml\"/></Relationships>";

    private static string SlideLayout() =>
        Declaration + "<p:sldLayout xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsR + "\" xmlns:p=\"" + NsP + "\" type=\"blank\" preserve=\"1\">" +
        "<p:cSld name=\"Blank\">" + EmptyTree + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string LayoutRels() =>
        Declaration + "<Relationships xmlns=\"" + RelNs + "\">" +
        "<Relationship Id=\"rId1\" Type=\"" + RelBase + "slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>";

    private static string Theme()
    {
        const string solid = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        const string effect = "<a:effectStyle><a:effectLst/></a:effectStyle>";
        static string Line(int w) => "<a:ln w=\"" + w + "\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">" + solid + "<a:prstDash val=\"solid\"/></a:ln>";
        return Declaration +
            "<a:theme xmlns:a=\"" + NsA + "\" name=\"ReportStudio\"><a:themeElements>" +
            "<a:clrScheme name=\"Office\">" +
            "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
            "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3>" +
            "<a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
            "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"Office\"><a:fillStyleLst>" + solid + solid + solid + "</a:fillStyleLst>" +
            "<a:lnStyleLst>" + Line(6350) + Line(12700) + Line(19050) + "</a:lnStyleLst>" +
            "<a:effectStyleLst>" + effect + effect + effect + "</a:effectStyleLst>" +
            "<a:bgFillStyleLst>" + solid + solid + solid + "</a:bgFillStyleLst></a:fmtScheme>" +
            "</a:themeElements></a:theme>";
    }

    // ---- shared helpers ----------------------------------------------------------------------

    /// <summary>Text for an attribute value or element content; characters XML forbids are dropped.</summary>
    internal static string Esc(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        sb.Append(ch).Append(text[++i]);
                    else if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ColorXml(System.Drawing.Color c, double opacity = 1)
    {
        double alpha = Math.Clamp(c.A / 255.0 * opacity, 0, 1);
        string hex = $"{c.R:X2}{c.G:X2}{c.B:X2}";
        return alpha >= 0.999
            ? "<a:srgbClr val=\"" + hex + "\"/>"
            : "<a:srgbClr val=\"" + hex + "\"><a:alpha val=\"" + (int)Math.Round(alpha * 100000) + "\"/></a:srgbClr>";
    }

    private static string SolidFill(System.Drawing.Color c, double opacity = 1) => "<a:solidFill>" + ColorXml(c, opacity) + "</a:solidFill>";

    private static string GradientFill(System.Drawing.Color from, System.Drawing.Color to, GradientDirection direction) =>
        "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\">" + ColorXml(from) + "</a:gs><a:gs pos=\"100000\">" + ColorXml(to) + "</a:gs></a:gsLst>" +
        "<a:lin ang=\"" + (direction == GradientDirection.LeftToRight ? 0 : 5400000) + "\" scaled=\"0\"/></a:gradFill>";

    /// <summary>The picture kinds PowerPoint can show as they are; null for anything else.</summary>
    private static string? PictureExtension(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8) return "jpeg";
        if (d.Length > 3 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return "gif";
        if (d.Length > 2 && d[0] == 0x42 && d[1] == 0x4D) return "bmp";
        if (d.Length > 4 && ((d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0) || (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0 && d[3] == 0x2A))) return "tiff";
        return null;
    }

    /// <summary>The pictures of the whole deck: one file for every distinct picture, however many slides show it.</summary>
    private sealed class Media
    {
        private readonly Dictionary<string, string> _byHash = new();
        private readonly HashSet<string> _extensions = new();
        public List<(string Name, byte[] Data)> Files { get; } = new();
        public IEnumerable<string> Extensions => _extensions;

        public string Add(byte[] data, string extension)
        {
            string hash = Convert.ToHexString(SHA256.HashData(data)) + "." + extension;
            if (_byHash.TryGetValue(hash, out var name)) return name;
            name = "image" + (Files.Count + 1).ToString(CultureInfo.InvariantCulture) + "." + extension;
            _byHash[hash] = name;
            _extensions.Add(extension);
            Files.Add((name, data));
            return name;
        }

        public static string ContentType(string extension) => extension switch
        {
            "png" => "image/png",
            "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    // ---- one slide ----------------------------------------------------------------------------

    private readonly record struct Look(bool Bold, bool Italic, bool Underline, bool Strike, string Color);

    private sealed class Slide
    {
        private readonly Media _media;
        private readonly RenderedPage _page;
        private readonly double _scale, _offsetX, _offsetY;
        private readonly StringBuilder _shapes = new();
        private readonly List<(string Id, string Type, string Target, bool External)> _rels = new();
        private readonly Dictionary<string, string> _linkIds = new();
        private int _nextShape = 2;

        public Slide(Media media, double deckW, double deckH, RenderedPage page)
        {
            _media = media;
            _page = page;
            double pw = page.Width > 0 ? page.Width : deckW;
            double ph = page.Height > 0 ? page.Height : deckH;
            _scale = Math.Min(deckW / pw, deckH / ph);
            if (!(_scale > 0) || double.IsInfinity(_scale)) _scale = 1;
            _offsetX = (deckW - pw * _scale) / 2;
            _offsetY = (deckH - ph * _scale) / 2;
        }

        private long L(double px) => (long)Math.Round(Math.Max(0, px) * _scale * EmuPerPx);
        private long X(double px) => (long)Math.Round((px * _scale + _offsetX) * EmuPerPx);
        private long Y(double px) => (long)Math.Round((px * _scale + _offsetY) * EmuPerPx);

        public (string Xml, string Rels) Build()
        {
            AddWatermark();
            foreach (var item in _page.Items) AddItem(item);

            string background = "";
            var bg = _page.Background;
            if (bg != null && ColorUtil.Parse(bg.Color) is { } back)
            {
                string fill = bg.GradientColor.Length > 0 && ColorUtil.Parse(bg.GradientColor) is { } second
                    ? GradientFill(back, second, bg.GradientDirection)
                    : SolidFill(back);
                background = "<p:bg><p:bgPr>" + fill + "<a:effectLst/></p:bgPr></p:bg>";
            }

            string xml = Declaration + "<p:sld xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsR + "\" xmlns:p=\"" + NsP + "\"><p:cSld>" +
                         background + EmptyTree + _shapes + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";

            var rels = new StringBuilder(Declaration);
            rels.Append("<Relationships xmlns=\"").Append(RelNs).Append("\">");
            rels.Append("<Relationship Id=\"rId1\" Type=\"").Append(RelBase).Append("slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>");
            foreach (var (id, type, target, external) in _rels)
                rels.Append("<Relationship Id=\"").Append(id).Append("\" Type=\"").Append(RelBase).Append(type).Append("\" Target=\"").Append(Esc(target)).Append('"')
                    .Append(external ? " TargetMode=\"External\"" : "").Append("/>");
            rels.Append("</Relationships>");
            return (xml, rels.ToString());
        }

        private string AddRelationship(string type, string target, bool external)
        {
            string id = "rId" + (_rels.Count + 2).ToString(CultureInfo.InvariantCulture);
            _rels.Add((id, type, target, external));
            return id;
        }

        // ---- building blocks

        /// <summary>The name part of a shape. A web, e-mail or phone link becomes a link on the shape.</summary>
        private string Nv(string kind, RenderedItem? item)
        {
            int id = _nextShape++;
            string head = "<p:cNvPr id=\"" + id + "\" name=\"" + kind + " " + id + "\"";
            var (linkKind, value) = item == null ? (LinkKind.None, "") : LinkTarget.Parse(item.Link);
            if (linkKind != LinkKind.External) return head + "/>";
            if (!_linkIds.TryGetValue(value, out var rid))
            {
                rid = AddRelationship("hyperlink", value, external: true);
                _linkIds[value] = rid;
            }
            return head + "><a:hlinkClick r:id=\"" + rid + "\"/></p:cNvPr>";
        }

        private string Xfrm(double x, double y, double w, double h, double clockwiseDegrees = 0)
        {
            string rot = Math.Abs(clockwiseDegrees) > 0.0001 ? " rot=\"" + N((long)Math.Round(clockwiseDegrees * 60000)) + "\"" : "";
            return "<a:xfrm" + rot + "><a:off x=\"" + N(X(x)) + "\" y=\"" + N(Y(y)) + "\"/><a:ext cx=\"" + N(L(w)) + "\" cy=\"" + N(L(h)) + "\"/></a:xfrm>";
        }

        private void Shape(RenderedItem? item, string kind, bool textBox, string xfrm, string geometry, string fill, string line, string? body)
        {
            _shapes.Append("<p:sp><p:nvSpPr>").Append(Nv(kind, item)).Append(textBox ? "<p:cNvSpPr txBox=\"1\"/>" : "<p:cNvSpPr/>")
                .Append("<p:nvPr/></p:nvSpPr><p:spPr>").Append(xfrm).Append(geometry).Append(fill).Append(line).Append("</p:spPr>");
            if (body != null) _shapes.Append(body);
            _shapes.Append("</p:sp>");
        }

        private static string FillXml(RenderedItem it)
        {
            var back = ColorUtil.Parse(it.BackColor);
            if (back == null) return NoFill;
            if (ShapeGeometry.UsesGradient(it) && ColorUtil.Parse(it.GradientColor) is { } second)
                return GradientFill(back.Value, second, it.GradientDirection);
            return SolidFill(back.Value);
        }

        private string LineXml(System.Drawing.Color? color, double widthPx)
        {
            if (color == null || !(widthPx > 0)) return NoLine;
            return "<a:ln w=\"" + N(Math.Max(1, L(widthPx))) + "\">" + SolidFill(color.Value) + "</a:ln>";
        }

        private static string RoundedRect(double radius, double w, double h)
        {
            double side = Math.Min(w, h);
            int adjust = side <= 0 ? 0 : (int)Math.Round(Math.Min(0.5, radius / side) * 100000);
            return "<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val " + adjust + "\"/></a:avLst></a:prstGeom>";
        }

        /// <summary>The fill and border of an element that has them, as a plain box behind it.</summary>
        private void Box(RenderedItem it)
        {
            var border = ColorUtil.Parse(it.BorderColor);
            bool hasBorder = border != null && it.BorderWidth > 0;
            if (ColorUtil.Parse(it.BackColor) == null && !hasBorder) return;
            Shape(null, "Box", false, Xfrm(it.X, it.Y, it.Width, it.Height), Rect, FillXml(it), LineXml(border, it.BorderWidth), null);
        }

        // ---- the elements

        private void AddItem(RenderedItem it)
        {
            switch (it.Kind)
            {
                case ItemKind.Line: AddLine(it); break;
                case ItemKind.Rectangle:
                case ItemKind.Ellipse: AddShape(it); break;
                case ItemKind.Image: AddPicture(it); break;
                case ItemKind.Barcode:
                    AddSymbol(it, BarcodeLayout.Bars(it.BarcodeType, it.Text, it.Width).Select(bar => (bar.Item1, 0.0, bar.Item2, it.Height)));
                    break;
                case ItemKind.QrCode: AddSymbol(it, QrLayout.Rectangles(it.Text, it.QrLevel, it.Width, it.Height)); break;
                case ItemKind.Barcode2D: AddSymbol(it, Symbol2DLayout.Rectangles(it.Barcode2DType, it.Text, it.Width, it.Height)); break;
                case ItemKind.Text: AddText(it); break;
            }
        }

        private void AddLine(RenderedItem it)
        {
            var color = ColorUtil.Parse(it.ForeColor) ?? System.Drawing.Color.Black;
            _shapes.Append("<p:cxnSp><p:nvCxnSpPr>").Append(Nv("Line", it)).Append("<p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>")
                .Append(Xfrm(it.X, it.Y, it.Width, it.Height)).Append("<a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>")
                .Append("<a:ln w=\"").Append(N(Math.Max(1, L(Math.Max(0.5, it.BorderWidth))))).Append("\">").Append(SolidFill(color)).Append("</a:ln></p:spPr></p:cxnSp>");
        }

        private void AddShape(RenderedItem it)
        {
            System.Drawing.Color edge = ColorUtil.Parse(it.BorderColor) ?? ColorUtil.Parse(it.ForeColor) ?? System.Drawing.Color.Black;
            string geometry;
            if (it.Kind == ItemKind.Ellipse) geometry = "<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom>";
            else
            {
                double radius = ShapeGeometry.CornerRadius(it.CornerRadius, it.Width, it.Height);
                geometry = radius > 0 ? RoundedRect(radius, it.Width, it.Height) : Rect;
            }
            System.Drawing.Color? lineColor = it.BorderWidth > 0 ? edge : null;
            Shape(it, it.Kind == ItemKind.Ellipse ? "Ellipse" : "Rectangle", false, Xfrm(it.X, it.Y, it.Width, it.Height), geometry,
                FillXml(it), LineXml(lineColor, it.BorderWidth), null);
        }

        private void AddText(RenderedItem it)
        {
            bool hasText = !string.IsNullOrEmpty(it.Text);
            var back = ColorUtil.Parse(it.BackColor);
            var border = ColorUtil.Parse(it.BorderColor);
            bool hasBorder = border != null && it.BorderWidth > 0;
            if (!hasText && back == null && !hasBorder) return;

            if (hasText && TextTurn.IsTurned(it.TextRotation))
            {
                // The fill and border stay upright; the text sits in its own box with the same middle, turned about it.
                Box(it);
                var (lw, lh) = TextTurn.LayoutSize(it.TextRotation, it.Width, it.Height);
                double cx = it.X + it.Width / 2, cy = it.Y + it.Height / 2;
                double clockwise = ((360 - TextTurn.Normalize(it.TextRotation)) % 360 + 360) % 360;
                Shape(it, "Text", true, Xfrm(cx - lw / 2, cy - lh / 2, lw, lh, clockwise), Rect, NoFill, NoLine, TextBody(it));
                return;
            }
            Shape(it, "Text", true, Xfrm(it.X, it.Y, it.Width, it.Height), Rect, FillXml(it), LineXml(border, it.BorderWidth), TextBody(it));
        }

        private string TextBody(RenderedItem it)
        {
            string anchor = it.VAlign switch { VAlign.Middle => "ctr", VAlign.Bottom => "b", _ => "t" };
            string align = it.HAlign switch { HAlign.Center => "ctr", HAlign.Right => "r", _ => "l" };
            long size = Math.Clamp((long)Math.Round(it.FontSize * _scale * 100), 100, 400000);
            string typeface = Esc(string.IsNullOrWhiteSpace(it.FontFamily) ? "Arial" : it.FontFamily.Trim());

            var sb = new StringBuilder("<p:txBody><a:bodyPr wrap=\"" + (it.WordWrap ? "square" : "none") + "\" lIns=\"" + N(L(2)) + "\" tIns=\"0\" rIns=\"" + N(L(2)) +
                                       "\" bIns=\"0\" anchor=\"" + anchor + "\" rtlCol=\"0\"><a:noAutofit/></a:bodyPr><a:lstStyle/>");
            foreach (var paragraph in Paragraphs(it))
            {
                sb.Append("<a:p><a:pPr algn=\"").Append(align).Append("\"/>");
                foreach (var (text, look) in paragraph)
                {
                    var color = ColorUtil.Parse(look.Color) ?? System.Drawing.Color.Black;
                    sb.Append("<a:r><a:rPr lang=\"en-US\" sz=\"").Append(N(size)).Append('"');
                    if (look.Bold) sb.Append(" b=\"1\"");
                    if (look.Italic) sb.Append(" i=\"1\"");
                    if (look.Underline) sb.Append(" u=\"sng\"");
                    if (look.Strike) sb.Append(" strike=\"sngStrike\"");
                    sb.Append(" dirty=\"0\">").Append(SolidFill(color)).Append("<a:latin typeface=\"").Append(typeface).Append("\"/><a:cs typeface=\"").Append(typeface)
                      .Append("\"/></a:rPr><a:t>").Append(Esc(text)).Append("</a:t></a:r>");
                }
                sb.Append("<a:endParaRPr lang=\"en-US\" sz=\"").Append(N(size)).Append("\" dirty=\"0\"/></a:p>");
            }
            sb.Append("</p:txBody>");
            return sb.ToString();
        }

        /// <summary>The text as paragraphs of runs: a line break starts a new paragraph, and rich text keeps the look of each run.</summary>
        private static List<List<(string Text, Look Look)>> Paragraphs(RenderedItem it)
        {
            var paragraphs = new List<List<(string Text, Look Look)>>();
            var current = new List<(string Text, Look Look)>();

            void AppendText(string text, Look look)
            {
                var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) { paragraphs.Add(current); current = new List<(string Text, Look Look)>(); }
                    if (parts[i].Length > 0) current.Add((parts[i], look));
                }
            }

            if (it.Runs is { Count: > 0 })
            {
                foreach (var run in it.Runs)
                    AppendText(run.Text ?? "", new Look(run.Bold || it.Bold, run.Italic || it.Italic, run.Underline || it.Underline, run.Strike,
                        string.IsNullOrWhiteSpace(run.Color) ? it.ForeColor : run.Color));
            }
            else AppendText(it.Text ?? "", new Look(it.Bold, it.Italic, it.Underline, false, it.ForeColor));

            paragraphs.Add(current);
            return paragraphs;
        }

        private void AddPicture(RenderedItem it)
        {
            Box(it);
            var data = it.Image;
            if (data == null || data.Length == 0) return;
            if (ImageData.LooksLikeSvg(data))
            {
                if (!ImageData.IsSafeSvg(data)) return;                  // an unsafe drawing is left out, like an unreadable picture
                data = SvgRaster.Resolve(data, it.Width, it.Height, 2);
                if (data.Length == 0) return;
            }
            string? extension = PictureExtension(data);
            if (extension == null) return;

            // The picture keeps its proportions inside the box and is centred in it.
            double x = it.X, y = it.Y, w = it.Width, h = it.Height;
            if (SvgRaster.Dimensions(data) is { } natural && natural.Width > 0 && natural.Height > 0 && it.Width > 0 && it.Height > 0)
            {
                double k = Math.Min(it.Width / natural.Width, it.Height / natural.Height);
                w = natural.Width * k; h = natural.Height * k;
                x = it.X + (it.Width - w) / 2; y = it.Y + (it.Height - h) / 2;
            }
            AppendPicture(it, "Picture", data, extension, Xfrm(x, y, w, h), 1);
        }

        private void AppendPicture(RenderedItem? item, string kind, byte[] data, string extension, string xfrm, double opacity)
        {
            string name = _media.Add(data, extension);
            string rid = AddRelationship("image", "../media/" + name, external: false);
            string blip = opacity < 0.999
                ? "<a:blip r:embed=\"" + rid + "\"><a:alphaModFix amt=\"" + (int)Math.Round(Math.Clamp(opacity, 0, 1) * 100000) + "\"/></a:blip>"
                : "<a:blip r:embed=\"" + rid + "\"/>";
            _shapes.Append("<p:pic><p:nvPicPr>").Append(Nv(kind, item)).Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
                .Append("<p:blipFill>").Append(blip).Append("<a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>").Append(xfrm).Append(Rect).Append("</p:spPr></p:pic>");
        }

        /// <summary>Barcodes and QR codes: all the bars or modules as the pieces of one drawn shape, so nothing shows hairline gaps.</summary>
        private void AddSymbol(RenderedItem it, IEnumerable<(double X, double Y, double W, double H)> pieces)
        {
            Box(it);
            var path = new StringBuilder();
            foreach (var (x, y, w, h) in pieces)
            {
                long x0 = L(x), y0 = L(y), x1 = L(x + w), y1 = L(y + h);
                if (x1 <= x0 || y1 <= y0) continue;
                path.Append("<a:moveTo><a:pt x=\"").Append(N(x0)).Append("\" y=\"").Append(N(y0)).Append("\"/></a:moveTo>")
                    .Append("<a:lnTo><a:pt x=\"").Append(N(x1)).Append("\" y=\"").Append(N(y0)).Append("\"/></a:lnTo>")
                    .Append("<a:lnTo><a:pt x=\"").Append(N(x1)).Append("\" y=\"").Append(N(y1)).Append("\"/></a:lnTo>")
                    .Append("<a:lnTo><a:pt x=\"").Append(N(x0)).Append("\" y=\"").Append(N(y1)).Append("\"/></a:lnTo><a:close/>");
            }
            if (path.Length == 0) return;

            long cx = L(it.Width), cy = L(it.Height);
            string geometry = "<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"" +
                              N(Math.Max(1, cx)) + "\" h=\"" + N(Math.Max(1, cy)) + "\">" + path + "</a:path></a:pathLst></a:custGeom>";
            var color = ColorUtil.Parse(it.ForeColor) ?? System.Drawing.Color.Black;
            Shape(it, "Code", false, Xfrm(it.X, it.Y, it.Width, it.Height), geometry, SolidFill(color), NoLine, null);
        }

        // ---- the watermark (behind everything else)

        private void AddWatermark()
        {
            var wm = _page.Watermark;
            if (wm == null) return;
            double opacity = Math.Clamp(wm.Opacity, 0, 1);
            double clockwise = ((360 - wm.Angle) % 360 + 360) % 360;
            double cx = _page.Width / 2, cy = _page.Height / 2;

            if (wm.Image is { Length: > 0 } picture && TryWatermarkPicture(picture, out var data, out var extension, out var natural))
            {
                var (w, h) = WatermarkLayout.PictureSize(natural.Width, natural.Height, _page.Width, _page.Height, wm.Angle, wm.ImageSize);
                if (w > 0 && h > 0)
                    AppendPicture(null, "Watermark", data, extension, Xfrm(cx - w / 2, cy - h / 2, w, h, clockwise), opacity);
            }

            if (string.IsNullOrWhiteSpace(wm.Text)) return;
            double room = WatermarkLayout.AvailableLength(_page.Width, _page.Height, wm.Angle) * WatermarkLayout.Fill;
            double wanted = wm.FontSize > 0 ? wm.FontSize : 72;
            double estimate = wm.Text.Length * wanted * 96.0 / 72.0 * (wm.Bold ? 0.62 : 0.55);   // a rough width, to shrink a long text to fit
            double points = WatermarkLayout.FitFontSize(wanted, estimate, _page.Width, _page.Height, wm.Angle);
            double boxHeight = points * 96.0 / 72.0 * 1.6;
            var color = ColorUtil.Parse(wm.Color) ?? System.Drawing.Color.Gray;
            long size100 = Math.Clamp((long)Math.Round(points * _scale * 100), 100, 400000);

            string body = "<p:txBody><a:bodyPr wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"ctr\" rtlCol=\"0\"><a:noAutofit/></a:bodyPr><a:lstStyle/>" +
                          "<a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr lang=\"en-US\" sz=\"" + N(size100) + "\"" + (wm.Bold ? " b=\"1\"" : "") + " dirty=\"0\">" +
                          SolidFill(color, opacity) + "<a:latin typeface=\"Arial\"/><a:cs typeface=\"Arial\"/></a:rPr><a:t>" + Esc(wm.Text) + "</a:t></a:r>" +
                          "<a:endParaRPr lang=\"en-US\" sz=\"" + N(size100) + "\" dirty=\"0\"/></a:p></p:txBody>";
            Shape(null, "Watermark", true, Xfrm(cx - room / 2, cy - boxHeight / 2, room, boxHeight, clockwise), Rect, NoFill, NoLine, body);
        }

        private static bool TryWatermarkPicture(byte[] picture, out byte[] data, out string extension, out (double Width, double Height) natural)
        {
            data = picture; extension = ""; natural = (0, 0);
            if (ImageData.LooksLikeSvg(picture))
            {
                if (!ImageData.IsSafeSvg(picture)) return false;
                data = SvgRaster.Resolve(picture, 1000, 1000, 1);
                if (data.Length == 0) return false;
            }
            var found = PictureExtension(data);
            if (found == null) return false;
            if (SvgRaster.Dimensions(data) is not { } dimensions) return false;
            extension = found;
            natural = dimensions;
            return true;
        }
    }
}
