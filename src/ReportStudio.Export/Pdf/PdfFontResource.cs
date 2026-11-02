using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// One font as a document uses it: a Type0 (CID-keyed) font with Identity-H encoding, so a character is two bytes
/// holding its glyph number. Remembers which glyphs were used and embeds only those, with a ToUnicode map so text
/// can still be searched and copied (the rupee sign U+20B9 included).
/// </summary>
internal sealed class PdfFontResource
{
    private readonly SortedDictionary<int, int> _used = new();    // glyph -> a character that produced it

    public PdfFontResource(TrueTypeFont font, string resourceName)
    {
        Font = font;
        ResourceName = resourceName;
    }

    public TrueTypeFont Font { get; }

    /// <summary>The name pages use for it, e.g. F1.</summary>
    public string ResourceName { get; }

    /// <summary>The text as the two-byte codes the content stream shows; the glyphs are noted for embedding.</summary>
    public byte[] Encode(string text)
    {
        var bytes = new List<byte>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            int glyph = Font.GlyphIndex(rune.Value);
            if (!_used.ContainsKey(glyph)) _used[glyph] = rune.Value;
            bytes.Add((byte)(glyph >> 8));
            bytes.Add((byte)glyph);
        }
        return bytes.ToArray();
    }

    /// <summary>Adds the font's objects to the file and returns the number of the Type0 font object.</summary>
    public int WriteTo(PdfFile file)
    {
        var glyphs = _used.Keys.ToList();
        var subset = Font.Subset(glyphs);
        var baseFont = SubsetTag(glyphs) + "+" + CleanName(Font.PostScriptName, Font.FamilyName);
        double scale = 1000.0 / Font.UnitsPerEm;

        var fontFile = file.Add(new PdfStream(new PdfDict().Put("Length1", subset.Length), subset));

        var descriptor = file.Add(new PdfDict()
            .PutName("Type", "FontDescriptor")
            .PutName("FontName", baseFont)
            .Put("Flags", 4)                                         // symbolic: glyphs are addressed by number
            .Put("FontBBox", PdfArray.Numbers(Font.XMin * scale, Font.YMin * scale, Font.XMax * scale, Font.YMax * scale))
            .Put("ItalicAngle", Font.ItalicAngle)
            .Put("Ascent", Font.Ascent * scale)
            .Put("Descent", -Font.Descent * scale)
            .Put("CapHeight", Font.CapHeight * scale)
            .Put("StemV", Font.IsBold ? 140 : 80)
            .Put("FontFile2", new PdfRef(fontFile)));

        var widths = new PdfArray();
        foreach (var glyph in glyphs)
            widths.Add(new PdfInt(glyph)).Add(new PdfArray().Add(new PdfInt((long)Math.Round(Font.AdvanceUnits(glyph) * scale))));

        var descendant = file.Add(new PdfDict()
            .PutName("Type", "Font")
            .PutName("Subtype", "CIDFontType2")
            .PutName("BaseFont", baseFont)
            .Put("CIDSystemInfo", new PdfDict()
                .Put("Registry", PdfString.Text("Adobe"))
                .Put("Ordering", PdfString.Text("Identity"))
                .Put("Supplement", 0))
            .Put("FontDescriptor", new PdfRef(descriptor))
            .Put("DW", 1000)
            .Put("W", widths)
            .PutName("CIDToGIDMap", "Identity"));

        var toUnicode = file.Add(new PdfStream(new PdfDict(), Encoding.ASCII.GetBytes(ToUnicodeMap())));

        return file.Add(new PdfDict()
            .PutName("Type", "Font")
            .PutName("Subtype", "Type0")
            .PutName("BaseFont", baseFont)
            .PutName("Encoding", "Identity-H")
            .Put("DescendantFonts", new PdfArray().Add(new PdfRef(descendant)))
            .Put("ToUnicode", new PdfRef(toUnicode)));
    }

    private string ToUnicodeMap()
    {
        var map = new StringBuilder();
        map.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        map.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        map.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        map.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var entries = _used.Where(e => e.Key != 0).ToList();       // glyph 0 is "missing": nothing to map back
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            map.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (glyph, character) in chunk)
            {
                var utf16 = Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(character));
                map.Append('<').Append(glyph.ToString("X4")).Append("> <").Append(Convert.ToHexString(utf16)).Append(">\n");
            }
            map.Append("endbfchar\n");
        }

        map.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return map.ToString();
    }

    /// <summary>Six capital letters that identify this subset, as the PDF standard asks for subset fonts.</summary>
    private string SubsetTag(IEnumerable<int> glyphs)
    {
        uint hash = 2166136261;
        foreach (var glyph in glyphs) hash = (hash ^ (uint)glyph) * 16777619;
        foreach (var ch in Font.PostScriptName) hash = (hash ^ ch) * 16777619;

        var tag = new char[6];
        for (int i = 0; i < 6; i++) { tag[i] = (char)('A' + hash % 26); hash /= 26; if (hash == 0) hash = 2166136261; }
        return new string(tag);
    }

    private static string CleanName(string postScriptName, string family)
    {
        var name = string.IsNullOrWhiteSpace(postScriptName) ? family : postScriptName;
        var clean = new string(name.Where(ch => ch < 128 && char.IsLetterOrDigit(ch) || ch == '-').ToArray());
        return clean.Length == 0 ? "Font" : clean;
    }
}
