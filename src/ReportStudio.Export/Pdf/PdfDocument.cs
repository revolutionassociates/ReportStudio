using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// Builds a PDF from pages drawn in report pixels (96 per inch, y down, origin at the top left).
/// Fonts and pictures are shared by all pages and written once; <see cref="Save"/> produces the file.
/// </summary>
internal sealed class PdfDocument
{
    private const double PointsPerPixel = 0.75;

    private readonly List<PdfPage> _pages = new();
    private readonly Dictionary<TrueTypeFont, PdfFontResource> _fonts = new();
    private readonly Dictionary<string, PdfImageResource> _images = new();

    private readonly Dictionary<string, (string Name, Color From, Color To, bool Horizontal)> _shadings = new();
    private readonly Dictionary<int, string> _opacities = new();       // percent -> graphics state name

    public PdfDocument(string title) { Title = title; }

    public string Title { get; }

    /// <summary>Write a tagged PDF: pages mark what they draw (set before the first page is added), and Save adds the structure tree.</summary>
    public bool Tagged { get; set; }

    public PdfPage AddPage(double widthPixels, double heightPixels)
    {
        var page = new PdfPage(widthPixels, heightPixels) { Tagging = Tagged };
        _pages.Add(page);
        return page;
    }

    public int PageCount => _pages.Count;

    private readonly List<(string Title, int Level, int Page, double Y)> _outline = new();

    /// <summary>Adds a line to the PDF reader's bookmark panel. Level 1 is a main heading; deeper levels nest under the line before them.</summary>
    public void Outline(string title, int level, int pageIndex, double y)
    {
        if (title.Length > 0) _outline.Add((title, Math.Max(1, level), pageIndex, y));
    }

    private readonly Dictionary<string, (int Page, double Y)> _bookmarks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names a place (page index from 0, height in pixels from the top) that links can jump to. The first use of a name wins.</summary>
    public void Bookmark(string name, int pageIndex, double y)
    {
        if (name.Length > 0 && !_bookmarks.ContainsKey(name)) _bookmarks[name] = (pageIndex, y);
    }

    public PdfFontResource Font(TrueTypeFont font)
    {
        if (!_fonts.TryGetValue(font, out var resource))
            _fonts[font] = resource = new PdfFontResource(font, "F" + (_fonts.Count + 1));
        return resource;
    }

    /// <summary>Registers a picture (once per <paramref name="key"/>) and returns the name pages draw it by.</summary>
    public string Image(string key, Func<Bitmap> load)
    {
        if (!_images.TryGetValue(key, out var image))
        {
            using var bitmap = load();
            _images[key] = image = PdfImageResource.From(bitmap, "Im" + (_images.Count + 1));
        }
        return image.ResourceName;
    }

    /// <summary>
    /// Registers a two-colour gradient (once per colour pair and direction) and returns the name pages paint it by.
    /// The gradient is defined on the unit square, so one definition serves every box: <see cref="PdfPage.FillGradient"/> moves and scales it.
    /// </summary>
    public string Shading(Color from, Color to, bool horizontal)
    {
        var key = $"{from.R:x2}{from.G:x2}{from.B:x2}-{to.R:x2}{to.G:x2}{to.B:x2}-{(horizontal ? 'h' : 'v')}";
        if (!_shadings.TryGetValue(key, out var shading))
            _shadings[key] = shading = ("Sh" + (_shadings.Count + 1), from, to, horizontal);
        return shading.Name;
    }

    /// <summary>
    /// Registers a see-through setting (once per whole percent) and returns the name <see cref="PdfPage.SetOpacity"/> switches it on by.
    /// 1 = solid, 0 = invisible; a value outside that range is cut to it.
    /// </summary>
    public string Opacity(double opacity)
    {
        int percent = double.IsNaN(opacity) ? 0 : (int)Math.Round(Math.Clamp(opacity, 0, 1) * 100);
        if (!_opacities.TryGetValue(percent, out var name))
            _opacities[percent] = name = "Gs" + (_opacities.Count + 1);
        return name;
    }

    private static PdfArray Rgb(Color color) => PdfArray.Numbers(color.R / 255.0, color.G / 255.0, color.B / 255.0);

    /// <summary>Writes the bookmark tree (outline) and returns the number of its root object, or 0 when there is none.</summary>
    private int WriteOutline(PdfFile file, List<int> pageRefs)
    {
        var entries = _outline.Where(e => e.Page >= 0 && e.Page < pageRefs.Count).ToList();
        if (entries.Count == 0) return 0;

        int n = entries.Count;
        var root = file.Reserve();
        var numbers = entries.Select(_ => file.Reserve()).ToList();

        // A level can be at most one deeper than the line before it (the first line is level 1).
        var level = new int[n];
        for (int i = 0; i < n; i++)
            level[i] = Math.Clamp(entries[i].Level, 1, i == 0 ? 1 : level[i - 1] + 1);

        var parent = new int[n];
        var children = new List<int>[n];
        var top = new List<int>();
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            children[i] = new List<int>();
            while (stack.Count > 0 && level[stack.Peek()] >= level[i]) stack.Pop();
            parent[i] = stack.Count > 0 ? stack.Peek() : -1;
            (parent[i] < 0 ? top : children[parent[i]]).Add(i);
            stack.Push(i);
        }

        int Descendants(int i) => children[i].Sum(c => 1 + Descendants(c));

        for (int i = 0; i < n; i++)
        {
            var siblings = parent[i] < 0 ? top : children[parent[i]];
            int at = siblings.IndexOf(i);
            double pageHeight = _pages[entries[i].Page].HeightPixels;
            var dict = new PdfDict()
                .Put("Title", PdfString.Text(entries[i].Title))
                .Put("Parent", new PdfRef(parent[i] < 0 ? root : numbers[parent[i]]))
                .Put("Dest", new PdfArray(new PdfValue[]
                {
                    new PdfRef(pageRefs[entries[i].Page]), new PdfName("XYZ"), new PdfReal(0),
                    new PdfReal(Math.Max(0, pageHeight - entries[i].Y) * PointsPerPixel), new PdfNull()
                }));
            if (at > 0) dict.Put("Prev", new PdfRef(numbers[siblings[at - 1]]));
            if (at < siblings.Count - 1) dict.Put("Next", new PdfRef(numbers[siblings[at + 1]]));
            if (children[i].Count > 0)
            {
                dict.Put("First", new PdfRef(numbers[children[i][0]]));
                dict.Put("Last", new PdfRef(numbers[children[i][^1]]));
                dict.Put("Count", Descendants(i));
            }
            file.Set(numbers[i], dict);
        }

        file.Set(root, new PdfDict()
            .PutName("Type", "Outlines")
            .Put("First", new PdfRef(numbers[top[0]]))
            .Put("Last", new PdfRef(numbers[top[^1]]))
            .Put("Count", n));
        return root;
    }

    public void Save(Stream stream, PdfOptions? options)
    {
        bool archive = options?.Conformance == PdfConformance.PdfA2b;
        if (archive && options!.NeedsEncryption)
            throw new InvalidOperationException("A PDF/A file cannot be encrypted: remove the passwords and restrictions, or do not ask for PDF/A.");

        if (_pages.Count == 0) AddPage(794, 1123);               // a PDF needs at least one page

        var file = new PdfFile();
        if (archive) file.Version = "1.7";
        file.Catalog = file.Reserve();
        var pages = file.Reserve();

        var fonts = new PdfDict();
        foreach (var font in _fonts.Values) fonts.Put(font.ResourceName, new PdfRef(font.WriteTo(file)));
        var images = new PdfDict();
        foreach (var image in _images.Values) images.Put(image.ResourceName, new PdfRef(image.WriteTo(file)));

        // Axial shading on the unit square: along x for left-to-right, along y (which runs downward on the page) for top-to-bottom.
        var shadings = new PdfDict();
        foreach (var shading in _shadings.Values)
        {
            var function = new PdfDict()
                .Put("FunctionType", 2).Put("Domain", PdfArray.Numbers(0, 1))
                .Put("C0", Rgb(shading.From)).Put("C1", Rgb(shading.To)).Put("N", 1);
            var dict = new PdfDict()
                .Put("ShadingType", 2).PutName("ColorSpace", "DeviceRGB")
                .Put("Coords", shading.Horizontal ? PdfArray.Numbers(0, 0, 1, 0) : PdfArray.Numbers(0, 0, 0, 1))
                .Put("Function", function)
                .Put("Extend", new PdfArray(new PdfValue[] { new PdfBool(true), new PdfBool(true) }));
            shadings.Put(shading.Name, new PdfRef(file.Add(dict)));
        }

        var resources = new PdfDict();
        if (_fonts.Count > 0) resources.Put("Font", fonts);
        if (_images.Count > 0) resources.Put("XObject", images);
        if (_shadings.Count > 0) resources.Put("Shading", shadings);

        // Graphics states that make what follows see-through: ca for fills (text), CA for strokes.
        var states = new PdfDict();
        foreach (var (percent, name) in _opacities)
            states.Put(name, new PdfDict().Put("ca", percent / 100.0).Put("CA", percent / 100.0));
        if (_opacities.Count > 0) resources.Put("ExtGState", states);
        var resourcesRef = file.Add(resources);

        var pageRefs = _pages.Select(_ => file.Reserve()).ToList();   // links may point to a later page
        var kids = new PdfArray();
        for (int index = 0; index < _pages.Count; index++)
        {
            var page = _pages[index];
            double width = page.WidthPixels * PointsPerPixel, height = page.HeightPixels * PointsPerPixel;
            var prefix = "q\n" + PdfFormat.Number(PointsPerPixel) + " 0 0 " + PdfFormat.Number(-PointsPerPixel) +
                         " 0 " + PdfFormat.Number(height) + " cm\n";
            var content = Encoding.Latin1.GetBytes(prefix + page.Content + "Q\n");
            var contentRef = file.Add(new PdfStream(new PdfDict(), content));

            var pageDict = new PdfDict()
                .PutName("Type", "Page")
                .Put("Parent", new PdfRef(pages))
                .Put("MediaBox", PdfArray.Numbers(0, 0, width, height))
                .Put("Resources", new PdfRef(resourcesRef))
                .Put("Contents", new PdfRef(contentRef));

            if (Tagged) pageDict.Put("StructParents", index).PutName("Tabs", "S");   // the page's entry in the parent tree; tab through links in structure order

            var annots = new PdfArray();
            foreach (var link in page.Links)
            {
                var annot = new PdfDict()
                    .PutName("Type", "Annot").PutName("Subtype", "Link")
                    .Put("Rect", PdfArray.Numbers(link.X * PointsPerPixel, height - (link.Y + link.Height) * PointsPerPixel,
                                                  (link.X + link.Width) * PointsPerPixel, height - link.Y * PointsPerPixel))
                    .Put("Border", PdfArray.Numbers(0, 0, 0))
                    .Put("F", 4);
                if (link.Uri.Length > 0)
                    annot.Put("A", new PdfDict().PutName("S", "URI").Put("URI", new PdfString(Encoding.ASCII.GetBytes(link.Uri))));
                else if (_bookmarks.TryGetValue(link.Bookmark, out var target) && target.Page < pageRefs.Count)
                {
                    double top = Math.Max(0, _pages[target.Page].HeightPixels - target.Y) * PointsPerPixel;
                    annot.Put("Dest", new PdfArray(new PdfValue[] { new PdfRef(pageRefs[target.Page]), new PdfName("XYZ"),
                                                                    new PdfReal(0), new PdfReal(top), new PdfNull() }));
                }
                else continue;   // a link to a bookmark that does not exist is not made clickable
                annots.Add(new PdfRef(file.Add(annot)));
            }
            if (annots.Items.Count > 0) pageDict.Put("Annots", annots);

            file.Set(pageRefs[index], pageDict);
            kids.Add(new PdfRef(pageRefs[index]));
        }

        file.Set(pages, new PdfDict()
            .PutName("Type", "Pages")
            .Put("Kids", kids)
            .Put("Count", _pages.Count));
        var catalog = new PdfDict()
            .PutName("Type", "Catalog")
            .Put("Pages", new PdfRef(pages));
        int outlineRoot = WriteOutline(file, pageRefs);
        if (outlineRoot != 0)
            catalog.Put("Outlines", new PdfRef(outlineRoot)).PutName("PageMode", "UseOutlines");
        if (Tagged) WriteStructure(file, catalog, pageRefs, options?.Language);
        var stamp = (options?.CreationTime ?? DateTime.UtcNow).ToUniversalTime();
        if (archive) WriteArchiveParts(file, catalog, stamp);
        file.Set(file.Catalog, catalog);

        // A PDF/A file repeats title, producer and dates in its XMP metadata and they must agree with this dictionary.
        var info = new PdfDict()
            .Put("Title", PdfString.Text(archive ? PdfXmp.CleanText(Title) : Title))
            .Put("Producer", PdfString.Text("ReportStudio"));
        if (archive)
            info.Put("CreationDate", PdfString.Text(PdfXmp.InfoDate(stamp))).Put("ModDate", PdfString.Text(PdfXmp.InfoDate(stamp)));
        file.Info = file.Add(info);

        file.Save(stream, options);
    }

    /// <summary>
    /// The structure tree of a tagged PDF: one Document element holding, page after page, an element for each piece of marked
    /// content (its number on the page is the MCID), and the parent tree that leads from a page's marked content back to them.
    /// </summary>
    private void WriteStructure(PdfFile file, PdfDict catalog, List<int> pageRefs, string? language)
    {
        var root = file.Reserve();
        var document = file.Reserve();
        var kids = new PdfArray();
        var numbers = new PdfArray();
        for (int p = 0; p < _pages.Count; p++)
        {
            var perPage = new PdfArray();
            foreach (var tag in _pages[p].Tags)
            {
                var element = new PdfDict()
                    .PutName("Type", "StructElem").PutName("S", tag.Structure)
                    .Put("P", new PdfRef(document)).Put("Pg", new PdfRef(pageRefs[p]))
                    .Put("K", tag.Mcid);
                if (tag.Alt.Length > 0) element.Put("Alt", PdfString.Text(tag.Alt));
                int number = file.Add(element);
                kids.Add(new PdfRef(number));
                perPage.Add(new PdfRef(number));
            }
            numbers.Add(new PdfInt(p)).Add(perPage);
        }

        file.Set(document, new PdfDict()
            .PutName("Type", "StructElem").PutName("S", "Document")
            .Put("P", new PdfRef(root)).Put("K", kids));
        file.Set(root, new PdfDict()
            .PutName("Type", "StructTreeRoot")
            .Put("K", new PdfRef(document))
            .Put("ParentTree", new PdfDict().Put("Nums", numbers)));

        catalog.Put("StructTreeRoot", new PdfRef(root))
            .Put("MarkInfo", new PdfDict().Put("Marked", true))
            .Put("Lang", PdfString.Text(string.IsNullOrWhiteSpace(language) ? "en" : language.Trim()))
            .Put("ViewerPreferences", new PdfDict().Put("DisplayDocTitle", true));
    }

    /// <summary>What PDF/A-2 asks of the file besides embedded fonts: XMP metadata, and an output intent that says the colours are sRGB.</summary>
    private void WriteArchiveParts(PdfFile file, PdfDict catalog, DateTime stamp)
    {
        var metadata = file.Add(new PdfStream(
            new PdfDict().PutName("Type", "Metadata").PutName("Subtype", "XML"),
            PdfXmp.Build(PdfXmp.CleanText(Title), "ReportStudio", stamp), compress: false));
        catalog.Put("Metadata", new PdfRef(metadata));

        var profile = file.Add(new PdfStream(new PdfDict().Put("N", 3), PdfIccProfile.SRgb()));
        var intent = file.Add(new PdfDict()
            .PutName("Type", "OutputIntent").PutName("S", "GTS_PDFA1")
            .Put("OutputConditionIdentifier", PdfString.Text(PdfIccProfile.Name))
            .Put("Info", PdfString.Text(PdfIccProfile.Name))
            .Put("DestOutputProfile", new PdfRef(profile)));
        catalog.Put("OutputIntents", new PdfArray().Add(new PdfRef(intent)));
    }
}

/// <summary>One page. All coordinates are report pixels, y growing downward.</summary>
internal sealed class PdfPage
{
    private readonly StringBuilder _content = new();

    public PdfPage(double widthPixels, double heightPixels)
    {
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
    }

    public double WidthPixels { get; }
    public double HeightPixels { get; }

    public string Content => _content.ToString();

    /// <summary>True for a tagged PDF: everything drawn is then wrapped in marked content, as page decoration or as a tagged element.</summary>
    public bool Tagging { get; init; }

    /// <summary>The tagged pieces of the page; the index in this list is the MCID in the content.</summary>
    public List<PdfTag> Tags { get; } = new();

    /// <summary>Starts a tagged piece (structure type such as P or Figure, with the alternative text of a figure). End it with <see cref="EndMarked"/>. Does nothing in an untagged PDF.</summary>
    public void BeginTag(string structure, string alt = "")
    {
        if (!Tagging) return;
        _content.Append('/').Append(structure).Append(" <</MCID ").Append(Tags.Count).Append(">> BDC\n");
        Tags.Add(new PdfTag(structure, Tags.Count, alt));
    }

    /// <summary>Starts content that is not part of the document's text (backgrounds, lines, boxes). End it with <see cref="EndMarked"/>.</summary>
    public void BeginArtifact()
    {
        if (Tagging) _content.Append("/Artifact BMC\n");
    }

    public void EndMarked()
    {
        if (Tagging) _content.Append("EMC\n");
    }

    /// <summary>Clickable areas of the page; each goes to a web address (<c>Uri</c>) or to a named place in the document (<c>Bookmark</c>).</summary>
    public List<PdfLinkArea> Links { get; } = new();

    public void LinkToUri(double x, double y, double width, double height, string uri) =>
        Links.Add(new PdfLinkArea(x, y, width, height, uri, ""));

    public void LinkToBookmark(double x, double y, double width, double height, string bookmark) =>
        Links.Add(new PdfLinkArea(x, y, width, height, "", bookmark));

    private static string N(double value) => PdfFormat.Number(value);

    private static string Rgb(Color color) =>
        N(color.R / 255.0) + " " + N(color.G / 255.0) + " " + N(color.B / 255.0);

    public void FillRectangle(double x, double y, double width, double height, Color color)
    {
        if (color.A == 0) return;
        _content.Append(Rgb(color)).Append(" rg ")
            .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re f\n");
    }

    /// <summary>The outline is centred on the rectangle's edge, like a GDI+ pen.</summary>
    public void StrokeRectangle(double x, double y, double width, double height, Color color, double lineWidth)
    {
        if (color.A == 0 || lineWidth <= 0) return;
        _content.Append(Rgb(color)).Append(" RG ").Append(N(lineWidth)).Append(" w ")
            .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re S\n");
    }

    // A quarter of an ellipse drawn as one Bezier curve is this far (fraction of the radius) from its end points.
    private const double Kappa = 0.5522847498307936;

    /// <summary>
    /// The outline of a box whose corners are quarter-ellipses of size <paramref name="rx"/> x <paramref name="ry"/>.
    /// With rx = width / 2 and ry = height / 2 it is an ellipse; with 0 it is a plain rectangle.
    /// </summary>
    private static string ShapePath(double x, double y, double width, double height, double rx, double ry)
    {
        double kx = rx * Kappa, ky = ry * Kappa, right = x + width, bottom = y + height;
        var p = new StringBuilder();
        void Move(double px, double py) => p.Append(N(px)).Append(' ').Append(N(py)).Append(" m\n");
        void LineTo(double px, double py) => p.Append(N(px)).Append(' ').Append(N(py)).Append(" l\n");
        void Curve(double x1, double y1, double x2, double y2, double x3, double y3) =>
            p.Append(N(x1)).Append(' ').Append(N(y1)).Append(' ').Append(N(x2)).Append(' ').Append(N(y2)).Append(' ')
             .Append(N(x3)).Append(' ').Append(N(y3)).Append(" c\n");

        Move(x + rx, y);
        LineTo(right - rx, y);
        Curve(right - rx + kx, y, right, y + ry - ky, right, y + ry);
        LineTo(right, bottom - ry);
        Curve(right, bottom - ry + ky, right - rx + kx, bottom, right - rx, bottom);
        LineTo(x + rx, bottom);
        Curve(x + rx - kx, bottom, x, bottom - ry + ky, x, bottom - ry);
        LineTo(x, y + ry);
        Curve(x, y + ry - ky, x + rx - kx, y, x + rx, y);
        p.Append("h\n");
        return p.ToString();
    }

    /// <summary>Fills a box with rounded corners (<paramref name="rx"/>, <paramref name="ry"/>) or, with half the width and height, an ellipse.</summary>
    public void FillShape(double x, double y, double width, double height, double rx, double ry, Color color)
    {
        if (color.A == 0 || width <= 0 || height <= 0) return;
        _content.Append(Rgb(color)).Append(" rg\n").Append(ShapePath(x, y, width, height, rx, ry)).Append("f\n");
    }

    /// <summary>
    /// Paints a gradient (from <see cref="PdfDocument.Shading"/>) inside a box with rounded corners (<paramref name="rx"/>, <paramref name="ry"/>),
    /// an ellipse (half the width and height) or, with 0, a plain rectangle: the shape clips, the shading is moved and scaled to the box.
    /// </summary>
    public void FillGradient(string shading, double x, double y, double width, double height, double rx, double ry)
    {
        if (width <= 0 || height <= 0) return;
        _content.Append("q\n");
        if (rx > 0 || ry > 0) _content.Append(ShapePath(x, y, width, height, rx, ry));
        else _content.Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re\n");
        _content.Append("W n\n")
            .Append(N(width)).Append(" 0 0 ").Append(N(height)).Append(' ').Append(N(x)).Append(' ').Append(N(y)).Append(" cm\n/")
            .Append(shading).Append(" sh\nQ\n");
    }

    /// <summary>The outline is centred on the edge, like a GDI+ pen.</summary>
    public void StrokeShape(double x, double y, double width, double height, double rx, double ry, Color color, double lineWidth)
    {
        if (color.A == 0 || lineWidth <= 0 || width <= 0 || height <= 0) return;
        _content.Append(Rgb(color)).Append(" RG ").Append(N(lineWidth)).Append(" w\n").Append(ShapePath(x, y, width, height, rx, ry)).Append("S\n");
    }

    public void Line(double x1, double y1, double x2, double y2, Color color, double lineWidth)
    {
        if (color.A == 0 || lineWidth <= 0) return;
        _content.Append(Rgb(color)).Append(" RG ").Append(N(lineWidth)).Append(" w ")
            .Append(N(x1)).Append(' ').Append(N(y1)).Append(" m ")
            .Append(N(x2)).Append(' ').Append(N(y2)).Append(" l S\n");
    }

    /// <summary>Fills closed shapes in one go: with the even-odd rule holes stay empty (letters such as O or the digit 8).</summary>
    public void FillPolygons(IEnumerable<IReadOnlyList<PointF>> shapes, Color color, bool evenOdd = true)
    {
        if (color.A == 0) return;
        var path = new StringBuilder();
        foreach (var shape in shapes)
        {
            if (shape.Count < 3) continue;
            path.Append(N(shape[0].X)).Append(' ').Append(N(shape[0].Y)).Append(" m\n");
            for (int i = 1; i < shape.Count; i++)
                path.Append(N(shape[i].X)).Append(' ').Append(N(shape[i].Y)).Append(" l\n");
            path.Append("h\n");
        }
        if (path.Length == 0) return;
        _content.Append(Rgb(color)).Append(" rg\n").Append(path).Append(evenOdd ? "f*\n" : "f\n");
    }

    /// <summary>Shows text with its baseline at <paramref name="baselineY"/>; the font size is in pixels.</summary>
    public void Text(PdfFontResource font, double sizePixels, double x, double baselineY, string text, Color color)
    {
        if (text.Length == 0 || color.A == 0) return;
        var codes = Convert.ToHexString(font.Encode(text));
        // The page is drawn y-down, so the text matrix flips the glyphs back upright.
        _content.Append("BT\n").Append(Rgb(color)).Append(" rg\n")
            .Append('/').Append(font.ResourceName).Append(' ').Append(N(sizePixels)).Append(" Tf\n")
            .Append("1 0 0 -1 ").Append(N(x)).Append(' ').Append(N(baselineY)).Append(" Tm\n")
            .Append('<').Append(codes).Append("> Tj\nET\n");
    }

    /// <summary>Everything drawn until <see cref="PopClip"/> is cut off outside the rectangle.</summary>
    public void PushClip(double x, double y, double width, double height) =>
        _content.Append("q\n").Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re W n\n");

    public void PopClip() => _content.Append("Q\n");

    /// <summary>
    /// Everything drawn until <see cref="PopRotation"/> is turned counter-clockwise (as seen on the page) by
    /// <paramref name="angleDegrees"/> about the point (<paramref name="centerX"/>, <paramref name="centerY"/>): that point becomes the origin,
    /// x runs along the turned baseline and y runs down the turned page, like everywhere else.
    /// </summary>
    public void PushRotation(double centerX, double centerY, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        double cos = TidyZero(Math.Cos(radians)), sin = TidyZero(Math.Sin(radians));
        _content.Append("q\n").Append(N(cos)).Append(' ').Append(N(TidyZero(-sin))).Append(' ').Append(N(sin)).Append(' ')
            .Append(N(cos)).Append(' ').Append(N(centerX)).Append(' ').Append(N(centerY)).Append(" cm\n");
    }

    public void PopRotation() => _content.Append("Q\n");

    /// <summary>Makes what follows see-through, using a name from <see cref="PdfDocument.Opacity"/>. Put it inside q/Q so it ends with them.</summary>
    public void SetOpacity(string name) => _content.Append('/').Append(name).Append(" gs\n");

    // Sine and cosine of a quarter turn come out as 6e-17; write a clean 0 (and never "-0").
    private static double TidyZero(double value) => Math.Abs(value) < 1e-9 ? 0 : value;

    public void Image(string resourceName, double x, double y, double width, double height) =>
        // A picture fills the unit square with y up; the matrix scales it and turns it over for the y-down page.
        _content.Append("q\n").Append(N(width)).Append(" 0 0 ").Append(N(-height)).Append(' ').Append(N(x)).Append(' ')
            .Append(N(y + height)).Append(" cm\n/").Append(resourceName).Append(" Do\nQ\n");
}

/// <summary>A picture stored as raw RGB, with its transparency as a separate gray image (soft mask).</summary>
internal sealed class PdfImageResource
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _rgb;
    private readonly byte[]? _alpha;

    private PdfImageResource(string name, int width, int height, byte[] rgb, byte[]? alpha)
    {
        ResourceName = name;
        _width = width;
        _height = height;
        _rgb = rgb;
        _alpha = alpha;
    }

    public string ResourceName { get; }

    public static PdfImageResource From(Bitmap bitmap, string name)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[Math.Abs(data.Stride) * height];
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }

        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        bool transparent = false;
        int stride = Math.Abs(data.Stride);
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int source = row * stride + col * 4, target = row * width + col;
                rgb[target * 3] = pixels[source + 2];      // memory order is B, G, R, A
                rgb[target * 3 + 1] = pixels[source + 1];
                rgb[target * 3 + 2] = pixels[source];
                alpha[target] = pixels[source + 3];
                if (pixels[source + 3] != 255) transparent = true;
            }
        }
        return new PdfImageResource(name, width, height, rgb, transparent ? alpha : null);
    }

    public int WriteTo(PdfFile file)
    {
        var dict = new PdfDict()
            .PutName("Type", "XObject")
            .PutName("Subtype", "Image")
            .Put("Width", _width)
            .Put("Height", _height)
            .PutName("ColorSpace", "DeviceRGB")
            .Put("BitsPerComponent", 8);

        if (_alpha != null)
        {
            var mask = file.Add(new PdfStream(new PdfDict()
                .PutName("Type", "XObject")
                .PutName("Subtype", "Image")
                .Put("Width", _width)
                .Put("Height", _height)
                .PutName("ColorSpace", "DeviceGray")
                .Put("BitsPerComponent", 8), _alpha));
            dict.Put("SMask", new PdfRef(mask));
        }
        return file.Add(new PdfStream(dict, _rgb));
    }
}

/// <summary>One tagged piece of a page: its structure type, its marked-content number and, for a figure, its alternative text.</summary>
internal sealed record PdfTag(string Structure, int Mcid, string Alt);

/// <summary>A clickable rectangle in report pixels.</summary>
internal sealed record PdfLinkArea(double X, double Y, double Width, double Height, string Uri, string Bookmark);
