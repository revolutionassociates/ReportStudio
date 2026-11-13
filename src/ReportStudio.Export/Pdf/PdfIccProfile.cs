using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// A small sRGB colour profile (ICC version 2.1, three colorants and three tone curves), made in code so the library needs no
/// profile file. PDF/A wants every file to say what its device colours mean (the "output intent"); the report draws in
/// DeviceRGB, which is sRGB for all practical purposes.
/// </summary>
internal static class PdfIccProfile
{
    public const string Name = "sRGB IEC61966-2.1";

    public static byte[] SRgb()
    {
        var curve = Curve();
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Description(Name)),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),          // the D50 white point profiles are connected through
            ("rXYZ", Xyz(0.4360, 0.2225, 0.0139)),      // sRGB primaries, adapted to D50
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
            ("rTRC", curve),
            ("gTRC", curve),
            ("bTRC", curve),
            ("cprt", Text("No copyright, use freely"))
        };

        // Tag data follows the tag table, each block on a 4-byte boundary; tags with the same data share one block.
        int tableEnd = 128 + 4 + 12 * tags.Count;
        var offsets = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var body = new List<byte>();
        foreach (var (_, data) in tags)
        {
            if (offsets.ContainsKey(data)) continue;
            offsets[data] = tableEnd + body.Count;
            body.AddRange(data);
            while (body.Count % 4 != 0) body.Add(0);
        }

        var profile = new List<byte>();
        Header(profile, (uint)(tableEnd + body.Count));
        U32(profile, (uint)tags.Count);
        foreach (var (signature, data) in tags)
        {
            profile.AddRange(Encoding.ASCII.GetBytes(signature));
            U32(profile, (uint)offsets[data]);
            U32(profile, (uint)data.Length);
        }
        profile.AddRange(body);
        return profile.ToArray();
    }

    private static void Header(List<byte> p, uint size)
    {
        U32(p, size);
        U32(p, 0);                                       // preferred CMM: none
        U32(p, 0x02100000);                              // version 2.1.0
        p.AddRange(Encoding.ASCII.GetBytes("mntr"));     // a display profile
        p.AddRange(Encoding.ASCII.GetBytes("RGB "));
        p.AddRange(Encoding.ASCII.GetBytes("XYZ "));     // connection space
        U16(p, 2024); U16(p, 1); U16(p, 1); U16(p, 0); U16(p, 0); U16(p, 0);   // creation date
        p.AddRange(Encoding.ASCII.GetBytes("acsp"));
        U32(p, 0);                                       // platform
        U32(p, 0);                                       // flags
        U32(p, 0);                                       // manufacturer
        U32(p, 0);                                       // model
        U32(p, 0); U32(p, 0);                            // attributes
        U32(p, 0);                                       // rendering intent: perceptual
        S15(p, 0.9642); S15(p, 1.0); S15(p, 0.8249);     // the illuminant (D50)
        U32(p, 0);                                       // creator
        p.AddRange(new byte[16]);                        // profile ID: not computed
        p.AddRange(new byte[28]);                        // reserved
    }

    /// <summary>The sRGB tone curve as 1024 steps.</summary>
    private static byte[] Curve()
    {
        const int count = 1024;
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("curv"));
        U32(data, 0);
        U32(data, count);
        for (int i = 0; i < count; i++)
        {
            double c = i / (double)(count - 1);
            double linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            U16(data, (ushort)Math.Round(Math.Clamp(linear, 0, 1) * 65535));
        }
        return data.ToArray();
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("XYZ "));
        U32(data, 0);
        S15(data, x); S15(data, y); S15(data, z);
        return data.ToArray();
    }

    /// <summary>The version 2 "textDescription" type: an ASCII name, then empty Unicode and Macintosh versions.</summary>
    private static byte[] Description(string text)
    {
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("desc"));
        U32(data, 0);
        U32(data, (uint)text.Length + 1);
        data.AddRange(Encoding.ASCII.GetBytes(text));
        data.Add(0);
        U32(data, 0);                                    // Unicode language code
        U32(data, 0);                                    // Unicode length
        U16(data, 0);                                    // Macintosh script code
        data.Add(0);                                     // Macintosh length
        data.AddRange(new byte[67]);
        return data.ToArray();
    }

    private static byte[] Text(string text)
    {
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("text"));
        U32(data, 0);
        data.AddRange(Encoding.ASCII.GetBytes(text));
        data.Add(0);
        return data.ToArray();
    }

    private static void U16(List<byte> p, ushort value) { p.Add((byte)(value >> 8)); p.Add((byte)value); }

    private static void U32(List<byte> p, uint value)
    {
        p.Add((byte)(value >> 24)); p.Add((byte)(value >> 16)); p.Add((byte)(value >> 8)); p.Add((byte)value);
    }

    /// <summary>A number as the profile format's signed 15.16 fixed point.</summary>
    private static void S15(List<byte> p, double value) => U32(p, unchecked((uint)(int)Math.Round(value * 65536)));
}
