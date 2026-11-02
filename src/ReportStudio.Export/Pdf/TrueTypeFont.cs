using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// Reads what the PDF writer needs from a TrueType font (glyf outlines only): metrics, the character map and the
/// names, and builds a small copy holding just the glyphs a document uses. Fonts with PostScript outlines (OTTO)
/// and font collections (.ttc) are refused.
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new();
    private readonly int[] _advances;
    private readonly bool _longLoca;
    private Dictionary<int, int>? _cmap;

    private TrueTypeFont(byte[] data)
    {
        _data = data;

        uint version = U32(0);
        if (version == 0x74746366) throw new NotSupportedException("Font collections (.ttc) are not supported.");
        if (version != 0x00010000 && version != 0x74727565)
            throw new NotSupportedException("Only TrueType fonts with glyf outlines are supported.");

        int count = U16(4);
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + i * 16;
            if (entry + 16 > data.Length) throw new InvalidDataException("The font's table directory is cut off.");
            var tag = Encoding.ASCII.GetString(data, entry, 4);
            long offset = U32(entry + 8), length = U32(entry + 12);
            if (offset + length > data.Length)
                throw new InvalidDataException($"Font table '{tag}' lies outside the file.");
            _tables[tag] = ((int)offset, (int)length);
        }

        foreach (var needed in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf" })
            if (!_tables.ContainsKey(needed)) throw new NotSupportedException($"The font has no '{needed}' table.");

        var head = _tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm == 0) throw new InvalidDataException("The font's units per em is zero.");
        _longLoca = I16(head + 50) != 0;
        XMin = I16(head + 36); YMin = I16(head + 38); XMax = I16(head + 40); YMax = I16(head + 42);
        int macStyle = U16(head + 44);
        IsBold = (macStyle & 1) != 0;
        IsItalic = (macStyle & 2) != 0;

        var hhea = _tables["hhea"].Offset;
        Ascent = I16(hhea + 4);
        Descent = -I16(hhea + 6);                       // stored negative; kept positive here
        LineGap = I16(hhea + 8);
        int metricCount = U16(hhea + 34);

        GlyphCount = U16(_tables["maxp"].Offset + 4);

        var hmtx = _tables["hmtx"].Offset;
        metricCount = Math.Min(metricCount, _tables["hmtx"].Length / 4);
        _advances = new int[Math.Max(GlyphCount, 1)];
        int last = 0;
        for (int g = 0; g < _advances.Length; g++)
        {
            if (g < metricCount) last = U16(hmtx + g * 4);
            _advances[g] = last;
        }

        if (_tables.TryGetValue("post", out var post) && post.Length >= 8)
            ItalicAngle = I16(post.Offset + 4) + U16(post.Offset + 6) / 65536.0;

        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && U16(os2.Offset) >= 2)
            CapHeight = I16(os2.Offset + 88);
        if (CapHeight <= 0) CapHeight = (int)(Ascent * 0.7);

        ReadNames();
    }

    public static TrueTypeFont Load(byte[] data) => new(data);

    public static TrueTypeFont Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>The raw file, for embedding when no subset is wanted.</summary>
    public byte[] Data => _data;

    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public int Ascent { get; }
    public int Descent { get; }
    public int LineGap { get; }
    public int CapHeight { get; private set; }
    public int XMin { get; }
    public int YMin { get; }
    public int XMax { get; }
    public int YMax { get; }
    public bool IsBold { get; }
    public bool IsItalic { get; }
    public double ItalicAngle { get; }

    public string FamilyName { get; private set; } = "";
    public string SubFamilyName { get; private set; } = "";
    public string PostScriptName { get; private set; } = "";

    /// <summary>Height of one line of text at <paramref name="size"/> (any unit): ascent + descent + line gap.</summary>
    public double LineHeight(double size) => (Ascent + Descent + LineGap) * size / UnitsPerEm;

    /// <summary>Distance from the top of a line to its baseline.</summary>
    public double BaselineOffset(double size) => (Ascent + LineGap / 2.0) * size / UnitsPerEm;

    /// <summary>The glyph for a character, or 0 (the font's "missing" glyph) when the font has none.</summary>
    public int GlyphIndex(int codePoint) => CharacterMap.TryGetValue(codePoint, out var gid) ? gid : 0;

    public bool HasGlyph(int codePoint) => GlyphIndex(codePoint) != 0;

    public int AdvanceUnits(int glyph) => glyph >= 0 && glyph < _advances.Length ? _advances[glyph] : 0;

    /// <summary>Width of the text at <paramref name="size"/>, summing the advance of each character (no kerning).</summary>
    public double Width(string text, double size)
    {
        long units = 0;
        foreach (var rune in text.EnumerateRunes()) units += AdvanceUnits(GlyphIndex(rune.Value));
        return units * size / UnitsPerEm;
    }

    // ---- character map ----

    private Dictionary<int, int> CharacterMap => _cmap ??= ReadCharacterMap();

    private Dictionary<int, int> ReadCharacterMap()
    {
        var map = new Dictionary<int, int>();
        if (!_tables.TryGetValue("cmap", out var cmap)) return map;

        int count = U16(cmap.Offset + 2);
        int best = -1, bestRank = -1;
        for (int i = 0; i < count; i++)
        {
            int rec = cmap.Offset + 4 + i * 8;
            int platform = U16(rec), encoding = U16(rec + 2);
            long sub = cmap.Offset + (long)U32(rec + 4);
            if (sub + 4 > _data.Length) continue;
            int format = U16((int)sub);

            int rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 4,      // Windows, full Unicode
                (0, _, 12) => 3,
                (3, 1, 4) => 2,        // Windows, Basic Multilingual Plane
                (0, _, 4) => 1,
                _ => -1
            };
            if (rank > bestRank) { best = (int)sub; bestRank = rank; }
        }
        if (best < 0) return map;

        if (U16(best) == 12) ReadFormat12(best, map);
        else ReadFormat4(best, map);
        return map;
    }

    private void ReadFormat4(int sub, Dictionary<int, int> map)
    {
        int segCount = U16(sub + 6) / 2;
        int ends = sub + 14, starts = ends + segCount * 2 + 2, deltas = starts + segCount * 2, ranges = deltas + segCount * 2;

        for (int s = 0; s < segCount; s++)
        {
            int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = I16(deltas + s * 2);
            int rangeOffset = U16(ranges + s * 2);

            for (int c = start; c <= end && c < 0xFFFF; c++)
            {
                int gid;
                if (rangeOffset == 0) gid = (c + delta) & 0xFFFF;
                else
                {
                    int at = ranges + s * 2 + rangeOffset + (c - start) * 2;
                    if (at + 2 > _data.Length) break;
                    gid = U16(at);
                    if (gid != 0) gid = (gid + delta) & 0xFFFF;
                }
                if (gid != 0 && gid < GlyphCount) map[c] = gid;
            }
        }
    }

    private void ReadFormat12(int sub, Dictionary<int, int> map)
    {
        uint groups = U32(sub + 12);
        for (uint g = 0; g < groups; g++)
        {
            long at = sub + 16L + g * 12;
            if (at + 12 > _data.Length) break;
            uint start = U32((int)at), end = U32((int)at + 4), first = U32((int)at + 8);
            if (end < start || end - start > 0x10FFFF) continue;
            for (uint c = start; c <= end; c++)
            {
                long gid = first + (c - start);
                if (gid != 0 && gid < GlyphCount) map[(int)c] = (int)gid;
            }
        }
    }

    // ---- names ----

    private void ReadNames()
    {
        if (!_tables.TryGetValue("name", out var name) || name.Length < 6) return;

        int count = U16(name.Offset + 2);
        int strings = name.Offset + U16(name.Offset + 4);
        var found = new Dictionary<int, (int Rank, string Text)>();

        for (int i = 0; i < count; i++)
        {
            int rec = name.Offset + 6 + i * 12;
            if (rec + 12 > _data.Length) break;
            int platform = U16(rec), encoding = U16(rec + 2), id = U16(rec + 6), length = U16(rec + 8), offset = U16(rec + 10);
            if (id != 1 && id != 2 && id != 6 && id != 16 && id != 17) continue;

            int at = strings + offset;
            if (at < 0 || at + length > _data.Length) continue;

            string text;
            int rank;
            if (platform == 3 || platform == 0) { text = Encoding.BigEndianUnicode.GetString(_data, at, length); rank = 2; }
            else if (platform == 1 && encoding == 0) { text = Encoding.Latin1.GetString(_data, at, length); rank = 1; }
            else continue;

            if (!found.TryGetValue(id, out var old) || rank > old.Rank) found[id] = (rank, text);
        }

        string Pick(params int[] ids) => ids.Select(i => found.TryGetValue(i, out var f) ? f.Text : "").FirstOrDefault(t => t.Length > 0) ?? "";

        FamilyName = Pick(16, 1);
        SubFamilyName = Pick(17, 2);
        PostScriptName = Pick(6);
    }

    // ---- subsetting ----

    /// <summary>
    /// A copy of the font in which only the given glyphs (and the parts of composite glyphs they use) keep their outlines.
    /// Glyph numbers stay the same, so a PDF can keep addressing glyphs by their original id. The character map,
    /// names and OS/2 data are left out: a PDF does not need them for a CID-keyed font.
    /// </summary>
    public byte[] Subset(IEnumerable<int> glyphs)
    {
        var keep = new SortedSet<int> { 0 };
        var pending = new Stack<int>(glyphs.Where(g => g > 0 && g < GlyphCount));
        while (pending.Count > 0)
        {
            int g = pending.Pop();
            if (!keep.Add(g)) continue;
            foreach (var part in Components(g)) if (!keep.Contains(part)) pending.Push(part);
        }

        var glyf = new MemoryStream();
        var loca = new byte[(GlyphCount + 1) * 4];
        for (int g = 0; g < GlyphCount; g++)
        {
            WriteU32(loca, g * 4, (uint)glyf.Length);
            if (keep.Contains(g))
            {
                var (start, length) = GlyphRange(g);
                glyf.Write(_data, start, length);
                while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            }
        }
        WriteU32(loca, GlyphCount * 4, (uint)glyf.Length);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = glyf.ToArray(),
            ["loca"] = loca,
        };
        foreach (var tag in new[] { "head", "hhea", "maxp", "hmtx", "cvt ", "fpgm", "prep" })
        {
            if (!_tables.TryGetValue(tag, out var t)) continue;
            var copy = new byte[t.Length];
            Buffer.BlockCopy(_data, t.Offset, copy, 0, t.Length);
            tables[tag] = copy;
        }
        tables["head"][50] = 0; tables["head"][51] = 1;          // loca is in the long format now
        for (int i = 8; i < 12; i++) tables["head"][i] = 0;      // checkSumAdjustment: viewers do not check it

        return Assemble(tables);
    }

    private (int Start, int Length) GlyphRange(int glyph)
    {
        var loca = _tables["loca"];
        var glyf = _tables["glyf"];
        long start, end;
        if (_longLoca)
        {
            if ((glyph + 2) * 4 > loca.Length) return (0, 0);
            start = U32(loca.Offset + glyph * 4); end = U32(loca.Offset + glyph * 4 + 4);
        }
        else
        {
            if ((glyph + 2) * 2 > loca.Length) return (0, 0);
            start = U16(loca.Offset + glyph * 2) * 2L; end = U16(loca.Offset + glyph * 2 + 2) * 2L;
        }
        if (end <= start || end > glyf.Length) return (0, 0);
        return (glyf.Offset + (int)start, (int)(end - start));
    }

    /// <summary>The glyphs a composite glyph is made of (none for a simple glyph).</summary>
    private IEnumerable<int> Components(int glyph)
    {
        var (start, length) = GlyphRange(glyph);
        if (length < 10 || I16(start) >= 0) yield break;

        int at = start + 10, end = start + length;
        while (at + 4 <= end)
        {
            int flags = U16(at), part = U16(at + 2);
            yield return part;
            at += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0) at += 2;
            else if ((flags & 0x0040) != 0) at += 4;
            else if ((flags & 0x0080) != 0) at += 8;
            if ((flags & 0x0020) == 0) break;
        }
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int entrySelector = 0;
        while ((1 << (entrySelector + 1)) <= count) entrySelector++;
        int searchRange = (1 << entrySelector) * 16;

        var output = new MemoryStream();
        void U16Out(int v) { output.WriteByte((byte)(v >> 8)); output.WriteByte((byte)v); }
        void U32Out(uint v) { U16Out((int)(v >> 16)); U16Out((int)(v & 0xFFFF)); }

        U32Out(0x00010000);
        U16Out(count); U16Out(searchRange); U16Out(entrySelector); U16Out(count * 16 - searchRange);

        int offset = 12 + count * 16;
        foreach (var (tag, data) in tables)
        {
            output.Write(Encoding.ASCII.GetBytes(tag));
            U32Out(Checksum(data));
            U32Out((uint)offset);
            U32Out((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        foreach (var data in tables.Values)
        {
            output.Write(data);
            for (int pad = data.Length; pad % 4 != 0; pad++) output.WriteByte(0);
        }
        return output.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : (uint)0);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    // ---- big-endian readers ----

    private int U16(int at) => at < 0 || at + 2 > _data.Length ? 0 : (_data[at] << 8) | _data[at + 1];

    private int I16(int at) => (short)U16(at);

    private uint U32(int at) =>
        at < 0 || at + 4 > _data.Length ? 0 : ((uint)_data[at] << 24) | ((uint)_data[at + 1] << 16) | ((uint)_data[at + 2] << 8) | _data[at + 3];

    private static void WriteU32(byte[] target, int at, uint value)
    {
        target[at] = (byte)(value >> 24); target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8); target[at + 3] = (byte)value;
    }
}
