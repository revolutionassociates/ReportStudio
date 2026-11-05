namespace ReportStudio.Core.Barcodes;

public enum BarcodeType
{
    Code128, Code39,
    /// <summary>EAN-13 (retail, 13 digits). Give 12 digits and the check digit is added, or 13 and it is checked.</summary>
    Ean13,
    /// <summary>EAN-8 (small retail items): 7 digits plus check digit, or 8.</summary>
    Ean8,
    /// <summary>UPC-A (North American retail): 11 digits plus check digit, or 12.</summary>
    UpcA,
    /// <summary>Interleaved 2 of 5: digits only, pairs; an odd count gets a leading 0 (ITF-14 cartons: 14 digits).</summary>
    Itf,
    /// <summary>Codabar (libraries, blood banks, parcels): 0-9 - $ : / . + with start/stop letter A-D at both ends (A is added if missing).</summary>
    Codabar
}

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

        return type switch
        {
            BarcodeType.Code39 => TryCode39(text, ref runs, ref error),
            BarcodeType.Ean13 or BarcodeType.UpcA or BarcodeType.Ean8 => TryRetail(type, text, ref runs, ref error),
            BarcodeType.Itf => TryItf(text, ref runs, ref error),
            BarcodeType.Codabar => TryCodabar(text, ref runs, ref error),
            _ => TryCode128(text, ref runs, ref error)
        };
    }

    /// <summary>
    /// The value as it should be read under the bars: an EAN/UPC with its check digit, an ITF with its leading zero,
    /// Code 39 in capitals. Returns the text unchanged when the value cannot be encoded.
    /// </summary>
    public static string PrintedText(BarcodeType type, string? text)
    {
        text ??= "";
        if (!TryEncode(type, text, out _, out _)) return text;
        return type switch
        {
            BarcodeType.Ean13 => CompleteDigits(text, 12),
            BarcodeType.UpcA => CompleteDigits(text, 11),
            BarcodeType.Ean8 => CompleteDigits(text, 7),
            BarcodeType.Itf => text.Length % 2 == 1 ? "0" + text : text,
            BarcodeType.Code39 or BarcodeType.Codabar => text.ToUpperInvariant(),
            _ => text
        };
    }

    /// <summary>A value that encodes in this symbology, for the designer to draw while the real value is still an {expression}.</summary>
    public static string SampleValue(BarcodeType type) => type switch
    {
        BarcodeType.Ean13 => "590123412345",
        BarcodeType.Ean8 => "9638507",
        BarcodeType.UpcA => "03600029145",
        BarcodeType.Codabar => "A12345B",
        _ => "1234567890"
    };

    /// <summary>Quiet zone each side in modules: EAN/UPC need a little more than the others.</summary>
    public static int QuietFor(BarcodeType type) =>
        type is BarcodeType.Ean13 or BarcodeType.Ean8 or BarcodeType.UpcA ? 11 : QuietModules;

    /// <summary>Total width in modules for this symbology, quiet zones included.</summary>
    public static int TotalModules(BarcodeType type, int[] runs) => runs.Sum() + 2 * QuietFor(type);

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

    // ---- EAN-13, UPC-A, EAN-8: seven modules per digit; 1 = bar, 0 = space ----
    private static readonly string[] EanL =
        { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
    private static readonly string[] EanG =
        { "0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111" };
    private static readonly string[] EanR =
        { "1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100" };
    /// <summary>EAN-13: which of the six left digits use the G set, chosen by the first digit (L = 0, G = 1).</summary>
    private static readonly string[] EanParity =
        { "000000", "001011", "001101", "001110", "010011", "011001", "011100", "010101", "010110", "011010" };

    /// <summary>The check digit of a digit string: weights 3, 1, 3, 1 ... counted from the right (valid for EAN-13, UPC-A and EAN-8).</summary>
    public static int CheckDigit(string payload)
    {
        int sum = 0;
        for (int i = 0; i < payload.Length; i++)
            sum += (payload[payload.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10;
    }

    /// <summary>The payload plus its check digit; a value that already has it (payload length + 1) is returned as it is.</summary>
    private static string CompleteDigits(string text, int payloadLength) =>
        text.Length == payloadLength ? text + CheckDigit(text) : text;

    private static bool TryRetail(BarcodeType type, string text, ref int[] runs, ref string error)
    {
        var (name, payload) = type switch
        {
            BarcodeType.Ean13 => ("EAN-13", 12),
            BarcodeType.UpcA => ("UPC-A", 11),
            _ => ("EAN-8", 7)
        };
        if (!text.All(c => c is >= '0' and <= '9') || (text.Length != payload && text.Length != payload + 1))
        {
            error = $"{name} needs {payload} digits (the check digit is added) or {payload + 1} digits";
            return false;
        }
        if (text.Length == payload + 1 && text[payload] - '0' != CheckDigit(text.Substring(0, payload)))
        {
            error = $"the check digit of this {name} should be {CheckDigit(text.Substring(0, payload))}";
            return false;
        }
        var full = CompleteDigits(text, payload);

        var bits = new System.Text.StringBuilder("101");
        if (type == BarcodeType.Ean8)
        {
            for (int i = 0; i < 4; i++) bits.Append(EanL[full[i] - '0']);
            bits.Append("01010");
            for (int i = 4; i < 8; i++) bits.Append(EanR[full[i] - '0']);
        }
        else
        {
            // UPC-A is an EAN-13 with a leading 0 that is not printed as a bar pattern of its own
            var ean = type == BarcodeType.UpcA ? "0" + full : full;
            var parity = EanParity[ean[0] - '0'];
            for (int i = 0; i < 6; i++) bits.Append((parity[i] == '1' ? EanG : EanL)[ean[i + 1] - '0']);
            bits.Append("01010");
            for (int i = 7; i < 13; i++) bits.Append(EanR[ean[i] - '0']);
        }
        bits.Append("101");
        runs = BitsToRuns(bits.ToString());
        return true;
    }

    /// <summary>Alternating bar and space widths (first is a bar) from a string of module bits.</summary>
    private static int[] BitsToRuns(string bits)
    {
        var result = new List<int>();
        int i = 0;
        while (i < bits.Length)
        {
            int j = i;
            while (j < bits.Length && bits[j] == bits[i]) j++;
            result.Add(j - i);
            i = j;
        }
        return result.ToArray();
    }

    // ---- Interleaved 2 of 5: n = narrow, w = wide, five elements per digit ----
    private static readonly string[] ItfPatterns =
        { "00110", "10001", "01001", "11000", "00101", "10100", "01100", "00011", "10010", "01010" };
    private const int ItfWide = 3;

    private static bool TryItf(string text, ref int[] runs, ref string error)
    {
        if (!text.All(c => c is >= '0' and <= '9')) { error = "Interleaved 2 of 5 can only hold digits"; return false; }
        if (text.Length % 2 == 1) text = "0" + text;

        var result = new List<int> { 1, 1, 1, 1 };   // start: narrow bar, space, bar, space
        for (int i = 0; i < text.Length; i += 2)
        {
            var bars = ItfPatterns[text[i] - '0'];
            var spaces = ItfPatterns[text[i + 1] - '0'];
            for (int k = 0; k < 5; k++)
            {
                result.Add(bars[k] == '1' ? ItfWide : 1);
                result.Add(spaces[k] == '1' ? ItfWide : 1);
            }
        }
        result.AddRange(new[] { ItfWide, 1, 1 });    // stop: wide bar, narrow space, narrow bar
        runs = result.ToArray();
        return true;
    }

    // ---- Codabar: seven elements per character (bar, space, ...), 1 = wide ----
    private const string CodabarChars = "0123456789-$:/.+ABCD";
    private static readonly string[] CodabarPatterns =
    {
        "0000011", "0000110", "0001001", "1100000", "0010010", "1000010", "0100001", "0100100", "0110000", "1001000",
        "0001100", "0011000", "1000101", "1010001", "1010100", "0010101", "0011010", "0101001", "0001011", "0001110"
    };
    private const int CodabarWide = 3;

    private static bool TryCodabar(string text, ref int[] runs, ref string error)
    {
        var value = text.ToUpperInvariant();
        bool framed = value.Length >= 2 && value[0] is >= 'A' and <= 'D' && value[^1] is >= 'A' and <= 'D';
        var body = framed ? value.Substring(1, value.Length - 2) : value;
        foreach (var c in body)
            if (CodabarChars.IndexOf(c) is < 0 or >= 16)
            {
                error = c is >= 'A' and <= 'D'
                    ? $"the letter {c} can only be a start or stop character: use A-D at both ends (A12345B)"
                    : $"character '{c}' cannot be written in Codabar";
                return false;
            }
        if (body.Length == 0) { error = "there is no value to encode"; return false; }

        var all = framed ? value : "A" + value + "A";
        var result = new List<int>();
        for (int n = 0; n < all.Length; n++)
        {
            if (n > 0) result.Add(1);   // narrow gap between characters
            foreach (var bit in CodabarPatterns[CodabarChars.IndexOf(all[n])])
                result.Add(bit == '1' ? CodabarWide : 1);
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

        double module = width / BarcodeEncoder.TotalModules(type, runs);
        double x = BarcodeEncoder.QuietFor(type) * module;
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
