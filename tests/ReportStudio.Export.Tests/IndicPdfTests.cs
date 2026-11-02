using System.Text;
using ReportStudio.Core.Model;
using ReportStudio.Core.Text;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Hindi and Tamil text in the PDF (requirement 15).</summary>
public class IndicPdfTests
{
    private static byte[] Make(params string[] lines)
    {
        var def = new ReportDefinition { Name = "Indic" };
        def.DataSource.Provider = DbProvider.Host;
        double y = 0;
        foreach (var line in lines)
        {
            def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = line, X = 0, Y = y, Width = 300, Height = 24, FontSize = 14 });
            y += 26;
        }
        def.Detail.Height = y;

        var rows = new[] { new Dictionary<string, object?> { ["Dummy"] = 1 } };
        return ReportRunner.ToPdf(def, rows);
    }

    private static int PageCount(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [Fact]
    public void Hindi_and_tamil_text_gives_a_valid_pdf()
    {
        var pdf = Make("नमस्ते, वेतन पर्ची", "வணக்கம், சம்பளச் சீட்டு", "Net pay: ₹ 12,34,567.00");

        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(pdf, 0, 4));
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void The_script_font_is_found_when_one_is_installed_or_shipped()
    {
        // Windows machines carry Nirmala UI; elsewhere the Noto fonts go in the Fonts folder. No font means the
        // old text path is used, which still gives a valid file, so this only checks the lookup is consistent.
        var family = PdfComplexText.FindFamily(IndicScript.Devanagari);
        if (family != null)
            Assert.Contains(family.Name, ComplexScript.PreferredFamilies(IndicScript.Devanagari), StringComparer.OrdinalIgnoreCase);
        Assert.Null(PdfComplexText.FindFamily(IndicScript.None));
    }

    [Fact]
    public void Latin_only_text_is_still_real_text()
    {
        var pdf = Make("Net pay");
        Assert.Equal(1, PageCount(pdf));
        // Real text carries an embedded font; drawn outlines would not.
        Assert.Contains("/FontFile", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void A_sample_is_written_for_checking_by_eye()
    {
        var family = PdfComplexText.FindFamily(IndicScript.Devanagari);
        if (family == null) return;                      // nothing to inspect without a font

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ReportStudio.sln"))) dir = dir.Parent;
        if (dir == null) return;                         // not run from the repository

        var pdf = Make("नमस्ते, वेतन पर्ची — क्षत्रिय ज्ञान श्री", "வணக்கம், சம்பளச் சீட்டு — ஸ்ரீ கிருஷ்ணன்", "Net pay: ₹ 12,34,567.00");
        var golden = Path.Combine(dir.FullName, "tests", "ReportStudio.Export.Tests", "Golden");
        Directory.CreateDirectory(golden);
        File.WriteAllBytes(Path.Combine(golden, "indic-sample.pdf"), pdf);
    }
}
