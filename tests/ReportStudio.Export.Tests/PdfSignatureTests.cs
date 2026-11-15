using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using ReportStudio.Export.Pdf;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Digitally signed PDF (requirement 43b). The tests sign with a self-signed certificate made on the spot.</summary>
public class PdfSignatureTests
{
    private static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ReportStudio Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, "test"), "test");
    }

    private static byte[] Make(PdfOptions? options)
    {
        var def = new ReportDefinition { Name = "Signed" };
        def.DataSource.Provider = DbProvider.Host;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "{Name}", Width = 200, Height = 20 });

        var rows = new[] { new Dictionary<string, object?> { ["Name"] = "Asha" } };
        return ReportRunner.ToPdf(def, rows, pdf: options);
    }

    private sealed record Parts(byte[] Signed, byte[] Cms);

    /// <summary>Reads the byte range, checks it fits the file, and returns the signed bytes and the signature.</summary>
    private static Parts Split(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var match = Regex.Match(text, @"/ByteRange \[0 (\d{10}) (\d{10}) (\d{10})\]");
        Assert.True(match.Success);
        int a = int.Parse(match.Groups[1].Value);
        int b = int.Parse(match.Groups[2].Value);
        int c = int.Parse(match.Groups[3].Value);

        Assert.Equal(pdf.Length, b + c);                       // the second range runs to the end of the file
        Assert.Equal((byte)'<', pdf[a]);                       // the gap is exactly the signature's hex string
        Assert.Equal((byte)'>', pdf[b - 1]);

        var signed = pdf[..a].Concat(pdf[b..]).ToArray();
        var padded = Convert.FromHexString(text.Substring(a + 1, b - a - 2));
        AsnDecoder.ReadEncodedValue(padded, AsnEncodingRules.BER, out _, out _, out int length);
        return new Parts(signed, padded[..length]);            // the rest is the zero padding
    }

    private static SignedCms Decode(Parts parts)
    {
        var cms = new SignedCms(new ContentInfo(parts.Signed), detached: true);
        cms.Decode(parts.Cms);
        return cms;
    }

    private static int PageCount(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [Fact]
    public void An_unsigned_pdf_has_no_signature()
    {
        var text = Encoding.Latin1.GetString(Make(null));

        Assert.DoesNotContain("/Type /Sig", text);
        Assert.DoesNotContain("/AcroForm", text);
        Assert.DoesNotContain("/ByteRange", text);
    }

    [Fact]
    public void A_signed_pdf_has_a_signature_field_and_a_byte_range_that_fits_the_file()
    {
        using var certificate = NewCertificate();
        var pdf = Make(new PdfOptions { SignWith = certificate });
        var text = Encoding.Latin1.GetString(pdf);

        Assert.Contains("/Type /Sig", text);
        Assert.Contains("/SubFilter /adbe.pkcs7.detached", text);
        Assert.Contains("/FT /Sig", text);
        Assert.Contains("/AcroForm", text);
        Assert.Contains("/SigFlags 3", text);

        var parts = Split(pdf);
        Assert.True(parts.Cms.Length < PdfSignature.Capacity);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void The_signature_verifies_and_names_the_certificate()
    {
        using var certificate = NewCertificate();
        var cms = Decode(Split(Make(new PdfOptions { SignWith = certificate })));

        cms.CheckSignature(verifySignatureOnly: true);          // throws when the signature does not match the bytes
        var signer = Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
        Assert.Equal(certificate.Thumbprint, signer.Certificate!.Thumbprint);
    }

    [Fact]
    public void Changing_the_file_afterwards_breaks_the_signature()
    {
        using var certificate = NewCertificate();
        var pdf = Make(new PdfOptions { SignWith = certificate });
        pdf[7] ^= 1;                                            // the version in the header line, inside the signed range

        var cms = Decode(Split(pdf));

        Assert.Throws<CryptographicException>(() => cms.CheckSignature(verifySignatureOnly: true));
    }

    [Fact]
    public void Reason_location_and_contact_are_written_into_the_signature()
    {
        using var certificate = NewCertificate();
        var text = Encoding.Latin1.GetString(Make(new PdfOptions
        {
            SignWith = certificate,
            SignReason = "Approved for payment",
            SignLocation = "Mumbai",
            SignContactInfo = "accounts@example.invalid"
        }));

        // strings are written as hexadecimal text
        Assert.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("Approved for payment")), text);
        Assert.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("Mumbai")), text);
        Assert.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("accounts@example.invalid")), text);
        Assert.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("ReportStudio Test")), text);   // the certificate's own name
    }

    [Fact]
    public void A_signed_pdf_cannot_be_encrypted()
    {
        using var certificate = NewCertificate();

        Assert.Throws<InvalidOperationException>(() =>
            Make(new PdfOptions { SignWith = certificate, UserPassword = "E1-2026" }));
    }

    [Fact]
    public void A_certificate_without_a_private_key_cannot_sign()
    {
        using var full = NewCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(full.RawData);

        Assert.Throws<InvalidOperationException>(() => Make(new PdfOptions { SignWith = publicOnly }));
    }

    [Fact]
    public void A_signed_archive_copy_with_tags_still_verifies()
    {
        using var certificate = NewCertificate();
        var pdf = Make(new PdfOptions { SignWith = certificate, Conformance = PdfConformance.PdfA2b, Tagged = true });

        Decode(Split(pdf)).CheckSignature(verifySignatureOnly: true);
        Assert.Contains("/GTS_PDFA1", Encoding.Latin1.GetString(pdf));
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void Signing_does_not_change_how_the_page_reads()
    {
        using var certificate = NewCertificate();
        using var plain = UglyToad.PdfPig.PdfDocument.Open(Make(null));
        using var signed = UglyToad.PdfPig.PdfDocument.Open(Make(new PdfOptions { SignWith = certificate }));

        Assert.Equal(plain.GetPage(1).Text, signed.GetPage(1).Text);
    }
}