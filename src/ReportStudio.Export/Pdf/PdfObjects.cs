using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ReportStudio.Export.Pdf;

// A small PDF object model: just what the report writer needs. Everything is written by PdfFile.Save.

/// <summary>Where a PDF is being written. Counts the position (the cross-reference table needs it) and encrypts on the way out.</summary>
internal sealed class PdfOutput
{
    private readonly Stream _stream;

    public PdfOutput(Stream stream, PdfEncryption? encryption)
    {
        _stream = stream;
        Encryption = encryption;
    }

    /// <summary>The file's encryption, or null for a plain file.</summary>
    public PdfEncryption? Encryption { get; }

    /// <summary>True while the encryption dictionary itself is written: it is never encrypted.</summary>
    public bool Suspended { get; set; }

    public PdfEncryption? Active => Suspended ? null : Encryption;

    public long Position { get; private set; }

    public void Write(byte[] data)
    {
        _stream.Write(data, 0, data.Length);
        Position += data.Length;
    }

    /// <summary>Writes text made of Latin-1 characters only (names, numbers, keywords).</summary>
    public void Write(string text) => Write(Encoding.Latin1.GetBytes(text));
}

internal abstract class PdfValue
{
    /// <summary>Writes the value. <paramref name="objectNumber"/> is the indirect object it sits in: encryption keys depend on it.</summary>
    public abstract void Write(PdfOutput output, int objectNumber);
}

internal static class PdfFormat
{
    /// <summary>A number the way PDF wants it: no exponent, no thousands separator, at most four decimals.</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}

internal sealed class PdfName : PdfValue
{
    public string Value { get; }
    public PdfName(string value) { Value = value; }

    public override void Write(PdfOutput output, int objectNumber)
    {
        var text = new StringBuilder("/");
        foreach (var ch in Value)
        {
            if (ch < '!' || ch > '~' || "()<>[]{}/%#".IndexOf(ch) >= 0)
                text.Append('#').Append(((int)ch & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
            else
                text.Append(ch);
        }
        output.Write(text.ToString());
    }
}

internal sealed class PdfInt : PdfValue
{
    public long Value { get; }
    public PdfInt(long value) { Value = value; }
    public override void Write(PdfOutput output, int objectNumber) =>
        output.Write(Value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class PdfReal : PdfValue
{
    public double Value { get; }
    public PdfReal(double value) { Value = value; }
    public override void Write(PdfOutput output, int objectNumber) => output.Write(PdfFormat.Number(Value));
}

internal sealed class PdfNull : PdfValue
{
    public override void Write(PdfOutput output, int objectNumber) => output.Write("null");
}

internal sealed class PdfBool : PdfValue
{
    public bool Value { get; }
    public PdfBool(bool value) { Value = value; }
    public override void Write(PdfOutput output, int objectNumber) => output.Write(Value ? "true" : "false");
}

/// <summary>A string, written in hexadecimal so no byte needs escaping. Encrypted when the file is.</summary>
internal sealed class PdfString : PdfValue
{
    public byte[] Bytes { get; }
    public PdfString(byte[] bytes) { Bytes = bytes; }

    /// <summary>Document text such as the title: plain ASCII when possible, otherwise UTF-16 with a byte-order mark.</summary>
    public static PdfString Text(string text)
    {
        if (text.All(ch => ch < 128)) return new PdfString(Encoding.ASCII.GetBytes(text));
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        return new PdfString(new byte[] { 0xFE, 0xFF }.Concat(utf16).ToArray());
    }

    public override void Write(PdfOutput output, int objectNumber)
    {
        var bytes = output.Active != null ? output.Active.Encrypt(Bytes, objectNumber) : Bytes;
        output.Write("<" + Convert.ToHexString(bytes) + ">");
    }
}

internal sealed class PdfRef : PdfValue
{
    public int Number { get; }
    public PdfRef(int number) { Number = number; }
    public override void Write(PdfOutput output, int objectNumber) =>
        output.Write(Number.ToString(CultureInfo.InvariantCulture) + " 0 R");
}

internal sealed class PdfArray : PdfValue
{
    public List<PdfValue> Items { get; } = new();

    public PdfArray() { }
    public PdfArray(IEnumerable<PdfValue> items) { Items.AddRange(items); }

    public static PdfArray Numbers(params double[] values) => new(values.Select(v => (PdfValue)new PdfReal(v)));

    public PdfArray Add(PdfValue value) { Items.Add(value); return this; }
    public PdfArray Add(double value) { Items.Add(new PdfReal(value)); return this; }

    public override void Write(PdfOutput output, int objectNumber)
    {
        output.Write("[");
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0) output.Write(" ");
            Items[i].Write(output, objectNumber);
        }
        output.Write("]");
    }
}

internal sealed class PdfDict : PdfValue
{
    private readonly List<KeyValuePair<string, PdfValue>> _entries = new();

    public PdfDict() { }
    public PdfDict(PdfDict copyOf) { _entries.AddRange(copyOf._entries); }

    public bool Contains(string key) => _entries.Any(e => e.Key == key);

    public PdfDict Put(string key, PdfValue value)
    {
        _entries.RemoveAll(e => e.Key == key);
        _entries.Add(new KeyValuePair<string, PdfValue>(key, value));
        return this;
    }

    public PdfDict Put(string key, int value) => Put(key, new PdfInt(value));
    public PdfDict Put(string key, double value) => Put(key, new PdfReal(value));
    public PdfDict Put(string key, bool value) => Put(key, new PdfBool(value));
    public PdfDict PutName(string key, string name) => Put(key, new PdfName(name));

    public override void Write(PdfOutput output, int objectNumber)
    {
        output.Write("<<");
        foreach (var (key, value) in _entries)
        {
            output.Write("\n");
            new PdfName(key).Write(output, objectNumber);
            output.Write(" ");
            value.Write(output, objectNumber);
        }
        output.Write("\n>>");
    }
}

/// <summary>A stream: a dictionary and bytes. Compressed with Flate unless the data is already encoded.</summary>
internal sealed class PdfStream : PdfValue
{
    public PdfDict Dict { get; }
    public byte[] Data { get; }
    public bool Compress { get; }

    public PdfStream(PdfDict dict, byte[] data, bool compress = true)
    {
        Dict = dict;
        Data = data;
        Compress = compress;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return buffer.ToArray();
    }

    public override void Write(PdfOutput output, int objectNumber)
    {
        var data = Data;
        var dict = new PdfDict(Dict);
        if (Compress && !dict.Contains("Filter"))
        {
            data = Deflate(data);
            dict.PutName("Filter", "FlateDecode");
        }
        if (output.Active != null) data = output.Active.Encrypt(data, objectNumber);
        dict.Put("Length", data.Length);

        dict.Write(output, objectNumber);
        output.Write("\nstream\n");
        output.Write(data);
        output.Write("\nendstream");
    }
}
