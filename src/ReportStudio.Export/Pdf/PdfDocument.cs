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

    public PdfDocument(string title) { Title = title; }

    public string Title { get; }

    public PdfPage AddPage(double widthPixels, double heightPixels)
    {
        var page = new PdfPage(widthPixels, heightPixels);
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
        if (_pages.Count == 0) AddPage(794, 1123);               // a PDF needs at least one page

        var file = new PdfFile();
        file.Catalog = file.Reserve();
        var pages = file.Reserve();

        var fonts = new PdfDict();
        foreach (var font in _fonts.Values) fonts.Put(font.ResourceName, new PdfRef(font.WriteTo(file)));
        var images = new PdfDict();
        foreach (var image in _images.Values) images.Put(image.ResourceName, new PdfRef(image.WriteTo(file)));

        var resources = new PdfDict();
        if (_fonts.Count > 0) resources.Put("Font", fonts);
        if (_images.Count > 0) resources.Put("XObject", images);
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
        file.Set(file.Catalog, catalog);
        file.Info = file.Add(new PdfDict()
            .Put("Title", PdfString.Text(Title))
            .Put("Producer", PdfString.Text("ReportStudio")));

        file.Save(stream, options);
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

/// <summary>A clickable rectangle in report pixels.</summary>
internal sealed record PdfLinkArea(double X, double Y, double Width, double Height, string Uri, string Bookmark);
