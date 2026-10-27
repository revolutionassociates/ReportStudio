namespace ReportStudio.Core.Barcodes;

public enum BarcodeType { Code128, Code39 }

/// <summary>
/// Encodes text as alternating bar and space widths (in modules, the first entry is a bar).
/// Code 128 uses set C for an all-digit value of even length (compact for IDs) and set B otherwise;
/// Code 39 accepts 0-9 A-Z - . space $ / + % (lower case is converted to upper case).
/// </summary>
public static class BarcodeEncoder
{
    /// <summary>Quiet zone kept clear on each side, in modules.</summary>
    public const int QuietModules = 10;

    // Code 128 symbols 0-105 as six element widths (bar, space, bar, space, bar, space), then the stop pattern.
    private static readonly string[] Code128Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212",
        "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131",
        "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321", "232121", "111323", "131123", "131321",
        "112313", "132113", "132311", "211313", "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121",
        "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114",
        "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111", "111242", "121142", "121241", "114212",
        "124112", "124211", "411212", "421112", "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113",
        "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412", "211214", "211232"
    };
    private const string Code128Stop = "2331112";
    private const int StartB = 104, StartC = 105;

    // Code 39: nine elements per character (bar, space, ...), 1 = wide. Same order as Code39Chars.
    private const string Code39Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%*";
    private static readonly string[] Code39Patterns =
    {
        "000110100", "100100001", "001100001", "101100000", "000110001", "100110000", "001110000", "000100101", "100100100",
        "001100100", "100001001", "001001001", "101001000", "000011001", "100011000", "001011000", "000001101", "100001100",
        "001001100", "000011100", "100000011", "001000011", "101000010", "000010011", "100010010", "001010010", "000000111",
        "100000110", "001000110", "000010110", "110000001", "011000001", "111000000", "010010001", "110010000", "011010000",
        "010000101", "110000100", "011000100", "010101000", "010100010", "010001010", "000101010", "010010100"
    };
    private const int Code39Wide = 3;

    /// <summary>Encodes the value. Returns false with a readable reason when it cannot be written in this symbology.</summary>
    public static bool TryEncode(BarcodeType type, string? text, out int[] runs, out string error)
    {
        runs = Array.Empty<int>();
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "there is no value to encode"; return false; }

        return type == BarcodeType.Code39 ? TryCode39(text, ref runs, ref error) : TryCode128(text, ref runs, ref error);
    }

    /// <summary>Total width in modules including both quiet zones.</summary>
    public static int TotalModules(int[] runs) => runs.Sum() + 2 * QuietModules;

    private static bool TryCode128(string text, ref int[] runs, ref string error)
    {
        var values = new List<int>();
        bool digits = text.Length >= 2 && text.Length % 2 == 0 && text.All(c => c is >= '0' and <= '9');

        if (digits)
        {
            values.Add(StartC);
            for (int i = 0; i < text.Length; i += 2) values.Add((text[i] - '0') * 10 + (text[i + 1] - '0'));
        }
        else
        {
            values.Add(StartB);
            foreach (var c in text)
            {
                if (c < 32 || c > 126) { error = $"character '{c}' cannot be written in Code 128"; return false; }
                values.Add(c - 32);
            }
        }

        int sum = values[0];
        for (int i = 1; i < values.Count; i++) sum += values[i] * i;
        values.Add(sum % 103);

        var result = new List<int>();
        foreach (var v in values) result.AddRange(Widths(Code128Patterns[v]));
        result.AddRange(Widths(Code128Stop));
        runs = result.ToArray();
        return true;
    }

    private static bool TryCode39(string text, ref int[] runs, ref string error)
    {
        var value = text.ToUpperInvariant();
        foreach (var c in value)
        {
            if (c == '*' || Code39Chars.IndexOf(c) < 0)
            {
                error = $"character '{c}' cannot be written in Code 39";
                return false;
            }
        }

        var result = new List<int>();
        var framed = "*" + value + "*";
        for (int n = 0; n < framed.Length; n++)
        {
            if (n > 0) result.Add(1);   // narrow gap between characters
            foreach (var bit in Code39Patterns[Code39Chars.IndexOf(framed[n])])
                result.Add(bit == '1' ? Code39Wide : 1);
        }
        runs = result.ToArray();
        return true;
    }

    private static IEnumerable<int> Widths(string digits) => digits.Select(d => d - '0');
}

/// <summary>Turns an encoded barcode into bar rectangles that fill a given width (quiet zones included).</summary>
public static class BarcodeLayout
{
    /// <summary>Bars as (x offset, width) from the left edge of a box <paramref name="width"/> wide. Empty if the value is invalid.</summary>
    public static IReadOnlyList<(double X, double Width)> Bars(BarcodeType type, string? text, double width)
    {
        if (!BarcodeEncoder.TryEncode(type, text, out var runs, out _) || width <= 0)
            return Array.Empty<(double, double)>();

        double module = width / BarcodeEncoder.TotalModules(runs);
        double x = BarcodeEncoder.QuietModules * module;
        var bars = new List<(double, double)>();
        for (int i = 0; i < runs.Length; i++)
        {
            double w = runs[i] * module;
            if (i % 2 == 0) bars.Add((x, w));
            x += w;
        }
        return bars;
    }
}
