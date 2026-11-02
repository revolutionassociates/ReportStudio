using System.Text;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Password and permission settings on the PDF output (requirement 12).</summary>
public class PdfSecurityTests
{
    private static byte[] Make(PdfOptions? options)
    {
        var def = new ReportDefinition { Name = "Secure" };
        def.DataSource.Provider = DbProvider.Host;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Name}", Width = 200, Height = 20 });

        var rows = new[] { new Dictionary<string, object?> { ["Name"] = "Asha" } };
        return ReportRunner.ToPdf(def, rows, pdf: options);
    }

    private static bool IsEncrypted(byte[] pdf) => Encoding.Latin1.GetString(pdf).Contains("/Encrypt");

    private static int PageCount(byte[] pdf, string? password)
    {
        using var doc = password == null
            ? UglyToad.PdfPig.PdfDocument.Open(pdf)
            : UglyToad.PdfPig.PdfDocument.Open(pdf, new UglyToad.PdfPig.ParsingOptions { Passwords = new[] { password } });
        return doc.NumberOfPages;
    }

    [Fact]
    public void A_plain_pdf_is_not_encrypted()
    {
        var pdf = Make(null);
        Assert.False(IsEncrypted(pdf));
        Assert.Equal(1, PageCount(pdf, null));
    }

    [Fact]
    public void Default_options_do_not_encrypt()
        => Assert.False(IsEncrypted(Make(new PdfOptions())));

    [Fact]
    public void A_user_password_is_needed_to_open_the_file()
    {
        var pdf = Make(new PdfOptions { UserPassword = "E1-2026" });

        Assert.True(IsEncrypted(pdf));
        Assert.Equal(1, PageCount(pdf, "E1-2026"));
        Assert.ThrowsAny<Exception>(() => PageCount(pdf, null));
    }

    [Fact]
    public void Restrictions_alone_encrypt_but_still_open_without_a_password()
    {
        var pdf = Make(new PdfOptions { AllowCopy = false, AllowModify = false });

        Assert.True(IsEncrypted(pdf));
        Assert.Equal(1, PageCount(pdf, null));
    }
}
