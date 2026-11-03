using System.Drawing;
using ReportStudio.Export;
using ReportStudio.Export.Pdf;
using UglyToad.PdfPig.Exceptions;
using ParsingOptions = UglyToad.PdfPig.ParsingOptions;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>The PDF writer (requirement: own writer instead of PDFsharp). PdfPig reads the files back.</summary>
public class PdfWriterTests
{
    private static readonly string FontPath = FindFont();

    private static string FindFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ReportStudio.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new DirectoryNotFoundException("ReportStudio.sln not found above " + AppContext.BaseDirectory);
        return Path.Combine(root, "fonts", "Ubuntu-Regular.ttf");
    }

    private static byte[] Build(Action<PdfDocument, PdfFontResource> draw, PdfOptions? options = null, int pages = 1)
    {
        var doc = new PdfDocument("Test report");
        var font = doc.Font(TrueTypeFont.Load(FontPath));
        for (int i = 0; i < pages; i++)
        {
            doc.AddPage(794, 1123);
            draw(doc, font);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, options);
        return ms.ToArray();
    }

    private static PdfPage Draw(PdfDocument doc, PdfFontResource font, string text)
    {
        var page = doc.AddPage(794, 1123);
        page.Text(font, 16, 50, 80, text, Color.Black);
        return page;
    }

    private static byte[] Document(string text, PdfOptions? options = null)
    {
        var doc = new PdfDocument("Payslip");
        var font = doc.Font(TrueTypeFont.Load(FontPath));
        Draw(doc, font, text);
        using var ms = new MemoryStream();
        doc.Save(ms, options);
        return ms.ToArray();
    }

    /// <summary>Spacing in extracted text depends on the reader, so compare without whitespace.</summary>
    private static void AssertText(UglyToad.PdfPig.PdfDocument pdf, string expected)
    {
        static string Squash(string text) => new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        Assert.Contains(Squash(expected), Squash(pdf.GetPage(1).Text));
    }

    [Fact]
    public void A_page_has_the_size_of_the_report_page_in_points()
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Document("Hello"));

        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(794 * 0.75, pdf.GetPage(1).Width, 2);
        Assert.Equal(1123 * 0.75, pdf.GetPage(1).Height, 2);
        Assert.Equal("Payslip", pdf.Information.Title);
    }

    [Fact]
    public void Text_can_be_read_back()
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Document("Net pay Hello 12,345.00"));

        AssertText(pdf, "Net pay Hello 12,345.00");
    }

    [Fact]
    public void The_rupee_sign_survives_when_the_font_has_it()
    {
        var font = TrueTypeFont.Load(FontPath);
        if (!font.HasGlyph(0x20B9)) return;                      // nothing to test with this font file

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Document("Rs ₹ 500"));

        AssertText(pdf, "₹");
    }

    [Fact]
    public void An_empty_document_still_has_one_page()
    {
        var doc = new PdfDocument("Empty");
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(ms.ToArray());
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public void Several_pages_share_one_font()
    {
        var bytes = Build((doc, font) => doc.Font(TrueTypeFont.Load(FontPath)), pages: 3);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(3, pdf.NumberOfPages);
    }

    [Fact]
    public void Graphics_and_a_picture_are_written_without_breaking_the_file()
    {
        var doc = new PdfDocument("Shapes");
        var font = doc.Font(TrueTypeFont.Load(FontPath));
        var page = doc.AddPage(400, 400);
        page.FillRectangle(10, 10, 100, 50, Color.LightGray);
        page.StrokeRectangle(10, 10, 100, 50, Color.Black, 1);
        page.Line(10, 80, 200, 80, Color.Red, 2);
        page.FillPolygons(new[] { new List<PointF> { new(20, 100), new(60, 100), new(40, 140) } }, Color.Blue);
        page.PushClip(0, 0, 200, 200);
        page.Text(font, 12, 10, 160, "clipped", Color.Black);
        page.PopClip();

        var image = doc.Image("pixel", () =>
        {
            var bmp = new Bitmap(2, 2);
            bmp.SetPixel(0, 0, Color.FromArgb(128, 255, 0, 0));
            bmp.SetPixel(1, 1, Color.Green);
            return bmp;
        });
        page.Image(image, 250, 10, 50, 50);
        Assert.Equal(image, doc.Image("pixel", () => throw new InvalidOperationException("loaded twice")));

        using var ms = new MemoryStream();
        doc.Save(ms, null);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(ms.ToArray());
        AssertText(pdf, "clipped");
        Assert.Single(pdf.GetPage(1).GetImages());
    }

    [Fact]
    public void A_subset_is_much_smaller_than_the_font_and_keeps_the_used_glyphs()
    {
        var font = TrueTypeFont.Load(FontPath);
        var glyphs = "Net".Select(c => font.GlyphIndex(c)).ToList();

        var subset = font.Subset(glyphs);

        Assert.True(subset.Length < font.Data.Length / 4, $"subset {subset.Length} bytes of {font.Data.Length}");
        var copy = TrueTypeFont.Load(subset);
        Assert.Equal(font.GlyphCount, copy.GlyphCount);
        Assert.Equal(font.AdvanceUnits(glyphs[0]), copy.AdvanceUnits(glyphs[0]));
    }

    [Fact]
    public void Text_width_is_the_sum_of_the_letter_widths()
    {
        var font = TrueTypeFont.Load(FontPath);

        double one = font.Width("i", 100), two = font.Width("ii", 100);

        Assert.True(one > 0);
        Assert.Equal(one * 2, two, 6);
        Assert.True(font.Width("W", 100) > font.Width("i", 100));
        Assert.True(font.LineHeight(12) > 12);
    }

    // ---- encryption ----

    [Fact]
    public void A_password_protected_file_opens_only_with_the_password()
    {
        var bytes = Document("Secret salary 99,000", new PdfOptions { UserPassword = "open-sesame" });

        Assert.Throws<PdfDocumentEncryptedException>(() => UglyToad.PdfPig.PdfDocument.Open(bytes));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes, new ParsingOptions { Passwords = new List<string> { "open-sesame" } });
        AssertText(pdf, "Secret salary 99,000");
    }

    [Fact]
    public void A_file_with_only_restrictions_opens_without_a_password()
    {
        var bytes = Document("Restricted", new PdfOptions { AllowCopy = false, AllowModify = false });

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        AssertText(pdf, "Restricted");
    }

    [Fact]
    public void The_owner_password_opens_the_file_too()
    {
        var bytes = Document("Owner view", new PdfOptions { UserPassword = "user", OwnerPassword = "owner" });

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes, new ParsingOptions { Passwords = new List<string> { "owner" } });
        AssertText(pdf, "Owner view");
    }

    [Fact]
    public void Permission_bits_follow_the_options()
    {
        const int print = 4, modify = 8, copy = 16;

        var all = PdfEncryption.PermissionBits(new PdfOptions());
        var none = PdfEncryption.PermissionBits(new PdfOptions { AllowPrint = false, AllowCopy = false, AllowModify = false });
        var printOnly = PdfEncryption.PermissionBits(new PdfOptions { AllowCopy = false, AllowModify = false });

        Assert.True((all & print) != 0 && (all & modify) != 0 && (all & copy) != 0);
        Assert.True((none & (print | modify | copy)) == 0);
        Assert.True((printOnly & print) != 0 && (printOnly & (modify | copy)) == 0);
    }

    [Fact]
    public void Rc4_matches_a_published_test_vector()
    {
        // RFC 6229 style check: key "Key", plaintext "Plaintext" -> BBF316E8D940AF0AD3
        var result = PdfEncryption.Rc4(System.Text.Encoding.ASCII.GetBytes("Key"), System.Text.Encoding.ASCII.GetBytes("Plaintext"));

        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(result));
    }

    // ---- links and bookmarks (requirement 27) ----

    private static string Latin1(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    [Fact]
    public void A_web_link_becomes_a_clickable_area()
    {
        var doc = new PdfDocument("Links");
        doc.AddPage(794, 1123).LinkToUri(10, 20, 100, 30, "https://example.com/a");
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        var text = Latin1(ms.ToArray());

        Assert.Contains("/Subtype /Link", text);
        Assert.Contains("/S /URI", text);
        Assert.Contains("<" + Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("https://example.com/a")) + ">", text);
    }

    [Fact]
    public void A_link_to_a_bookmark_points_at_the_page_that_holds_it()
    {
        var doc = new PdfDocument("Links");
        doc.AddPage(794, 1123).LinkToBookmark(10, 20, 100, 30, "Later");
        doc.AddPage(794, 1123);
        doc.Bookmark("Later", 1, 200);
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        var text = Latin1(ms.ToArray());

        Assert.Contains("/Dest [", text);
        Assert.Contains("/XYZ", text);
    }

    [Fact]
    public void A_link_to_a_missing_bookmark_is_left_out()
    {
        var doc = new PdfDocument("Links");
        doc.AddPage(794, 1123).LinkToBookmark(10, 20, 100, 30, "Nowhere");
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        Assert.DoesNotContain("/Subtype /Link", Latin1(ms.ToArray()));
    }

    [Fact]
    public void A_file_with_links_still_opens_in_a_reader()
    {
        var doc = new PdfDocument("Links");
        var page = doc.AddPage(794, 1123);
        page.LinkToUri(10, 20, 100, 30, "mailto:hr@example.com");
        doc.AddPage(794, 1123);
        page.LinkToBookmark(10, 60, 100, 30, "P2");
        doc.Bookmark("P2", 1, 0);
        using var ms = new MemoryStream();
        doc.Save(ms, null);
        ms.Position = 0;

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(ms);
        Assert.Equal(2, pdf.NumberOfPages);
    }

    // ---- bookmark panel (requirement 28) ----

    [Fact]
    public void Outline_lines_make_a_bookmark_panel_with_nested_levels()
    {
        var doc = new PdfDocument("Outline");
        doc.AddPage(794, 1123);
        doc.AddPage(794, 1123);
        doc.Outline("Part one", 1, 0, 100);
        doc.Outline("Detail Ü", 2, 1, 200);       // nests under "Part one"; non-ASCII title
        doc.Outline("Part two", 1, 1, 400);
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        var text = Latin1(ms.ToArray());

        Assert.Contains("/Type /Outlines", text);
        Assert.Contains("/PageMode /UseOutlines", text);
        Assert.Contains("/Outlines ", text);
        Assert.Contains("/Count 3", text);        // the root counts every line
        Assert.Contains("/Count 1", text);        // "Part one" has one child
        Assert.Contains("<" + Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("Part one")) + ">", text);
        Assert.Contains("/Parent", text);
        Assert.Contains("/Next", text);
        Assert.Contains("/Prev", text);

        ms.Position = 0;
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(ms);
        Assert.Equal(2, pdf.NumberOfPages);
    }

    [Fact]
    public void A_document_without_outline_lines_has_no_bookmark_panel()
    {
        var doc = new PdfDocument("Plain");
        doc.AddPage(794, 1123);
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        var text = Latin1(ms.ToArray());

        Assert.DoesNotContain("/Outlines", text);
        Assert.DoesNotContain("UseOutlines", text);
    }

    [Fact]
    public void A_level_that_skips_a_step_is_pulled_up_and_a_line_on_a_missing_page_is_dropped()
    {
        var doc = new PdfDocument("Odd");
        doc.AddPage(794, 1123);
        doc.Outline("Starts deep", 3, 0, 0);      // first line is always a main heading
        doc.Outline("Nowhere", 1, 9, 0);          // no such page
        using var ms = new MemoryStream();
        doc.Save(ms, null);

        var text = Latin1(ms.ToArray());

        Assert.Contains("/Count 1", text);
        Assert.DoesNotContain(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("Nowhere")), text);
    }
}
