using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>Text written exactly as it is (the signature placeholders, whose length must not change).</summary>
internal sealed class PdfRaw : PdfValue
{
    public string Text { get; }
    public PdfRaw(string text) { Text = text; }
    public override void Write(PdfOutput output, int objectNumber) => output.Write(Text);
}

/// <summary>
/// Signs a finished PDF with a certificate: the usual "detached PKCS#7" signature (adbe.pkcs7.detached) that Adobe Reader and
/// other viewers check. The file is written with an empty signature of a fixed size and a byte range of fixed width; once
/// the final bytes are known, the byte range is filled in, everything outside the signature is signed, and the signature
/// is written into its place. The file keeps its length, so no offset in it changes.
/// </summary>
internal static class PdfSignature
{
    /// <summary>Bytes reserved for the signature. A signature with a certificate chain is 3 to 8 KB.</summary>
    public const int Capacity = 12288;

    private const string ByteRangeBlank = "[0 0000000000 0000000000 0000000000]";
    private static readonly string ContentsBlank = "<" + new string('0', Capacity * 2) + ">";

    /// <summary>Adds the signature and its (invisible) form field; returns the field's object number, which belongs in page 1's annotations and the AcroForm.</summary>
    public static int AddField(PdfFile file, int pageRef, PdfOptions options, DateTime stamp)
    {
        var signature = new PdfDict()
            .PutName("Type", "Sig")
            .PutName("Filter", "Adobe.PPKLite")
            .PutName("SubFilter", "adbe.pkcs7.detached")
            .Put("ByteRange", new PdfRaw(ByteRangeBlank))
            .Put("Contents", new PdfRaw(ContentsBlank))
            .Put("M", PdfString.Text(PdfXmp.InfoDate(stamp)));

        string name = !string.IsNullOrWhiteSpace(options.SignerName)
            ? options.SignerName!
            : options.SignWith?.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        if (name.Length > 0) signature.Put("Name", PdfString.Text(name));
        if (!string.IsNullOrWhiteSpace(options.SignReason)) signature.Put("Reason", PdfString.Text(options.SignReason!));
        if (!string.IsNullOrWhiteSpace(options.SignLocation)) signature.Put("Location", PdfString.Text(options.SignLocation!));
        if (!string.IsNullOrWhiteSpace(options.SignContactInfo)) signature.Put("ContactInfo", PdfString.Text(options.SignContactInfo!));
        int signatureNumber = file.Add(signature);

        // An invisible field: an empty rectangle. 132 = print + locked.
        return file.Add(new PdfDict()
            .PutName("Type", "Annot").PutName("Subtype", "Widget").PutName("FT", "Sig")
            .Put("T", PdfString.Text("Signature1"))
            .Put("V", new PdfRef(signatureNumber))
            .Put("Rect", PdfArray.Numbers(0, 0, 0, 0))
            .Put("F", 132)
            .Put("P", new PdfRef(pageRef)));
    }

    /// <summary>Fills in the byte range and the signature of a file written with <see cref="AddField"/>. The array is changed in place and returned.</summary>
    public static byte[] Sign(byte[] pdf, X509Certificate2 certificate, DateTime stamp)
    {
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("The signing certificate has no private key, so it cannot sign. Use a certificate file (.pfx) or a store certificate that includes its private key.");

        const string RangeKey = "/ByteRange ";
        const string ContentsKey = "/Contents ";
        int rangeAt = pdf.AsSpan().IndexOf(Encoding.ASCII.GetBytes(RangeKey + ByteRangeBlank));
        int contentsAt = pdf.AsSpan().IndexOf(Encoding.ASCII.GetBytes(ContentsKey + ContentsBlank));
        if (rangeAt < 0 || contentsAt < 0)
            throw new InvalidOperationException("The signature placeholder is missing from the PDF.");

        int start = contentsAt + ContentsKey.Length;            // the '<' that opens the signature
        int end = start + ContentsBlank.Length;                 // the byte after the '>' that closes it
        var inv = CultureInfo.InvariantCulture;
        string range = "[0 " + start.ToString("D10", inv) + " " + end.ToString("D10", inv) + " " + (pdf.Length - end).ToString("D10", inv) + "]";
        Encoding.ASCII.GetBytes(range).CopyTo(pdf, rangeAt + RangeKey.Length);

        // What is signed: the whole file except the signature itself.
        var signed = new byte[start + (pdf.Length - end)];
        Buffer.BlockCopy(pdf, 0, signed, 0, start);
        Buffer.BlockCopy(pdf, end, signed, start, pdf.Length - end);

        byte[]? cms = null;
        foreach (var include in new[] { X509IncludeOption.WholeChain, X509IncludeOption.EndCertOnly })
        {
            var candidate = Encode(signed, certificate, include, stamp);
            if (candidate.Length <= Capacity)
            {
                cms = candidate;
                break;
            }
        }
        if (cms == null)
            throw new InvalidOperationException("The signature (with the certificate) is larger than the " + Capacity + " bytes reserved for it.");

        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(pdf, start + 1);
        return pdf;
    }

    private static byte[] Encode(byte[] data, X509Certificate2 certificate, X509IncludeOption include, DateTime stamp)
    {
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),   // SHA-256
            IncludeOption = include
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(stamp));
        cms.ComputeSignature(signer, silent: true);               // never ask the user for a PIN window
        return cms.Encode();
    }
}
