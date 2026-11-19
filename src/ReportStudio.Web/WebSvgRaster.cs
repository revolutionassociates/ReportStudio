using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;

namespace ReportStudio.Export;

/// <summary>
/// The web viewer's stand-in for the Export project's SvgRaster (which draws with GDI+): the linked HtmlExporter only asks it how big a
/// picture is, to size a watermark picture. The size is read from the file header (PNG, GIF, BMP, JPEG) or from the width, height or viewBox
/// of an SVG, so nothing is decoded or drawn. null means "cannot tell", and the picture is then left out.
/// </summary>
internal static class SvgRaster
{
    private static readonly Regex ViewBox = new(
        @"viewBox\s*=\s*[""']\s*([-+\d.eE]+)[\s,]+([-+\d.eE]+)[\s,]+([-+\d.eE]+)[\s,]+([-+\d.eE]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex WidthAttribute = new(@"\swidth\s*=\s*[""']\s*([\d.]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HeightAttribute = new(@"\sheight\s*=\s*[""']\s*([\d.]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static (double Width, double Height)? Dimensions(byte[] picture)
    {
        if (picture == null || picture.Length < 10) return null;
        if (ImageData.LooksLikeSvg(picture)) return SvgSize(picture);
        return PngSize(picture) ?? GifSize(picture) ?? BmpSize(picture) ?? JpegSize(picture);
    }

    internal static (double, double)? PngSize(byte[] d)
    {
        if (d.Length < 24 || d[0] != 0x89 || d[1] != 0x50 || d[2] != 0x4E || d[3] != 0x47) return null;
        return Valid(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20)));
    }

    internal static (double, double)? GifSize(byte[] d)
    {
        if (d.Length < 10 || d[0] != (byte)'G' || d[1] != (byte)'I' || d[2] != (byte)'F') return null;
        return Valid(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)));
    }

    internal static (double, double)? BmpSize(byte[] d)
    {
        if (d.Length < 26 || d[0] != (byte)'B' || d[1] != (byte)'M') return null;
        long w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
        long h = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22)));
        return Valid(w, h);
    }

    internal static (double, double)? JpegSize(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 9 < d.Length)
        {
            if (d[pos] != 0xFF) { pos++; continue; }
            int marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }
            if (marker == 0xD9) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 2));
            bool startOfFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (startOfFrame)
                return Valid(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 7)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 5)));
            if (length < 2) return null;
            pos += 2 + length;
        }
        return null;
    }

    internal static (double, double)? SvgSize(byte[] d)
    {
        var head = Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, 4096));
        int start = head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int end = head.IndexOf('>', start);
        var tag = end < 0 ? head[start..] : head[start..end];

        try
        {
            var w = WidthAttribute.Match(tag);
            var h = HeightAttribute.Match(tag);
            if (w.Success && h.Success && Number(w.Groups[1].Value) is { } ww && Number(h.Groups[1].Value) is { } hh && ww > 0 && hh > 0)
                return (ww, hh);

            var box = ViewBox.Match(tag);
            if (box.Success && Number(box.Groups[3].Value) is { } bw && Number(box.Groups[4].Value) is { } bh && bw > 0 && bh > 0)
                return (bw, bh);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        return null;
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static (double, double)? Valid(long width, long height) =>
        width > 0 && height > 0 && width <= 100_000 && height <= 100_000 ? (width, height) : null;
}
