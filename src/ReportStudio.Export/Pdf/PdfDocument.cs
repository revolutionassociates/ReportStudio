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

        var kids = new PdfArray();
        foreach (var page in _pages)
        {
            double width = page.WidthPixels * PointsPerPixel, height = page.HeightPixels * PointsPerPixel;
            var prefix = "q\n" + PdfFormat.Number(PointsPerPixel) + " 0 0 " + PdfFormat.Number(-PointsPerPixel) +
                         " 0 " + PdfFormat.Number(height) + " cm\n";
            var content = Encoding.Latin1.GetBytes(prefix + page.Content + "Q\n");
            var contentRef = file.Add(new PdfStream(new PdfDict(), content));

            kids.Add(new PdfRef(file.Add(new PdfDict()
                .PutName("Type", "Page")
                .Put("Parent", new PdfRef(pages))
                .Put("MediaBox", PdfArray.Numbers(0, 0, width, height))
                .Put("Resources", new PdfRef(resourcesRef))
                .Put("Contents", new PdfRef(contentRef)))));
        }

        file.Set(pages, new PdfDict()
            .PutName("Type", "Pages")
            .Put("Kids", kids)
            .Put("Count", _pages.Count));
        file.Set(file.Catalog, new PdfDict()
            .PutName("Type", "Catalog")
            .Put("Pages", new PdfRef(pages)));
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
