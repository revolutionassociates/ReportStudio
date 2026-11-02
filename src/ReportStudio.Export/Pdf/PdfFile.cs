using System.Globalization;
using System.Security.Cryptography;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// The skeleton of a PDF file: numbered objects, a header, a cross-reference table and a trailer.
/// Objects are added first (so references can be made before the content exists) and written in one go by Save.
/// </summary>
internal sealed class PdfFile
{
    private readonly List<PdfValue?> _objects = new();

    /// <summary>The /Root of the file: the catalog.</summary>
    public int Catalog { get; set; }

    /// <summary>The /Info dictionary (title and so on), or 0.</summary>
    public int Info { get; set; }

    /// <summary>Reserves an object number; the value is filled in later with <see cref="Set"/>.</summary>
    public int Reserve()
    {
        _objects.Add(null);
        return _objects.Count;
    }

    public int Add(PdfValue value)
    {
        _objects.Add(value);
        return _objects.Count;
    }

    public void Set(int number, PdfValue value) => _objects[number - 1] = value;

    public void Save(Stream stream, PdfOptions? options)
    {
        if (Catalog == 0) throw new InvalidOperationException("The PDF has no catalog.");

        var fileId = RandomNumberGenerator.GetBytes(16);
        var encryption = options != null && options.NeedsEncryption ? new PdfEncryption(options, fileId) : null;
        var output = new PdfOutput(stream, encryption);

        // The second line holds bytes above 127 so file transfer tools treat the file as binary.
        output.Write("%PDF-1.6\n");
        output.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var offsets = new long[_objects.Count + 1];
        for (int i = 0; i < _objects.Count; i++)
        {
            var number = i + 1;
            offsets[number] = output.Position;
            output.Write(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            (_objects[i] ?? throw new InvalidOperationException($"PDF object {number} was reserved but never set."))
                .Write(output, number);
            output.Write("\nendobj\n");
        }

        // The encryption dictionary is an object of its own, written without encryption.
        int encryptNumber = 0;
        if (encryption != null)
        {
            encryptNumber = _objects.Count + 1;
            offsets = offsets.Append(output.Position).ToArray();
            output.Write(encryptNumber.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            output.Suspended = true;
            encryption.ToDictionary().Write(output, encryptNumber);
            output.Suspended = false;
            output.Write("\nendobj\n");
        }

        var total = offsets.Length;                      // entries 0 .. count
        var xref = output.Position;
        output.Write("xref\n0 " + total.ToString(CultureInfo.InvariantCulture) + "\n");
        output.Write("0000000000 65535 f \n");           // each entry is exactly 20 bytes
        for (int number = 1; number < total; number++)
            output.Write(offsets[number].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        var id = "<" + Convert.ToHexString(fileId) + ">";
        var trailer = "trailer\n<<\n/Size " + total.ToString(CultureInfo.InvariantCulture) +
                      "\n/Root " + Catalog.ToString(CultureInfo.InvariantCulture) + " 0 R";
        if (Info != 0) trailer += "\n/Info " + Info.ToString(CultureInfo.InvariantCulture) + " 0 R";
        if (encryptNumber != 0) trailer += "\n/Encrypt " + encryptNumber.ToString(CultureInfo.InvariantCulture) + " 0 R";
        trailer += "\n/ID [" + id + id + "]\n>>\nstartxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n";
        output.Write(trailer);
    }
}
