using System.Text;

namespace ReportStudio.Core.Barcodes;

/// <summary>
/// Data Matrix ECC 200 encoder (ISO/IEC 16022): square symbols from 10 x 10 to 48 x 48 modules, ASCII encodation
/// (digit pairs are packed, so numbers take half the space) and Reed-Solomon error correction. One data block per symbol;
/// the largest symbol holds 174 codewords: about 173 characters or 348 digits.
/// Text with characters above 255 is written as UTF-8 bytes; text up to 255 as ISO 8859-1 (the default of readers).
/// </summary>
public static class DataMatrixEncoder
{
    private sealed record SymbolSize(int Size, int RegionsPerSide, int DataCodewords, int EccCodewords)
    {
        /// <summary>Data modules along one side of a data region (the border of finder and clock modules not counted).</summary>
        public int RegionData => (Size - 2 * RegionsPerSide) / RegionsPerSide;
        /// <summary>Data modules along one side of the whole symbol, as the placement sees it.</summary>
        public int DataSide => RegionData * RegionsPerSide;
    }

    private static readonly SymbolSize[] Sizes =
    {
        new(10, 1, 3, 5), new(12, 1, 5, 7), new(14, 1, 8, 10), new(16, 1, 12, 12), new(18, 1, 18, 14),
        new(20, 1, 22, 18), new(22, 1, 30, 20), new(24, 1, 36, 24), new(26, 1, 44, 28),
        new(32, 2, 62, 36), new(36, 2, 86, 42), new(40, 2, 114, 48), new(44, 2, 144, 56), new(48, 2, 174, 68)
    };

    /// <summary>The most codewords one symbol holds.</summary>
    public static int MaxCodewords => Sizes[^1].DataCodewords;

    /// <summary>Encodes the text. Returns false with a readable reason when it is empty or does not fit the largest symbol.</summary>
    public static bool TryEncode(string? text, out bool[][] modules, out string error)
    {
        modules = Array.Empty<bool[]>();
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "there is no value to encode"; return false; }

        var words = AsciiCodewords(text);
        var size = Sizes.FirstOrDefault(s => s.DataCodewords >= words.Count);
        if (size == null)
        {
            error = $"the value is too long for a Data Matrix (it needs {words.Count} codewords, at most {MaxCodewords} fit; " +
                    "digits take half the space)";
            return false;
        }

        modules = Build(size, Pad(words, size.DataCodewords));
        return true;
    }

    /// <summary>The symbol side in modules (10, 12 ... 48) the text needs, or 0 when it does not fit or is empty.</summary>
    public static int SymbolSizeFor(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = AsciiCodewords(text).Count;
        return Sizes.FirstOrDefault(s => s.DataCodewords >= count)?.Size ?? 0;
    }

    // ---- encodation ----

    /// <summary>The text as bytes: ISO 8859-1 when every character fits, otherwise UTF-8.</summary>
    private static byte[] ToBytes(string text) =>
        text.All(c => c <= 255) ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// ASCII encodation: two digits in a row become one codeword (130 + the pair), a character 0-127 becomes its code + 1,
    /// and a byte 128-255 becomes the upper-shift codeword 235 followed by (byte - 127).
    /// </summary>
    public static List<int> AsciiCodewords(string text)
    {
        var bytes = ToBytes(text);
        var words = new List<int>();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (IsDigit(b) && i + 1 < bytes.Length && IsDigit(bytes[i + 1]))
            {
                words.Add(130 + (b - '0') * 10 + (bytes[i + 1] - '0'));
                i++;
            }
            else if (b < 128) words.Add(b + 1);
            else { words.Add(235); words.Add(b - 127); }
        }
        return words;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>Fills the unused room: the first pad codeword is 129, the others follow a fixed scrambling by position.</summary>
    private static List<int> Pad(List<int> words, int capacity)
    {
        var result = new List<int>(words);
        if (result.Count < capacity) result.Add(129);
        while (result.Count < capacity)
        {
            int position = result.Count + 1;                       // 1-based position of this pad codeword
            int pad = 129 + 149 * position % 253 + 1;
            result.Add(pad <= 254 ? pad : pad - 254);
        }
        return result;
    }

    // ---- Reed-Solomon over GF(256), polynomial x^8 + x^5 + x^3 + x^2 + 1, roots 2^1 ... 2^n ----

    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];

    static DataMatrixEncoder()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = x;
            Log[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x12D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    /// <summary>The error correction codewords for the data codewords (highest power first).</summary>
    public static int[] ErrorCorrection(IReadOnlyList<int> data, int count)
    {
        // generator polynomial: the product of (x + 2^i) for i = 1..count, highest power first
        var g = new int[] { 1 };
        for (int i = 1; i <= count; i++)
        {
            var next = new int[g.Length + 1];
            for (int j = 0; j < g.Length; j++)
            {
                next[j] ^= g[j];
                next[j + 1] ^= Multiply(g[j], Exp[i]);
            }
            g = next;
        }

        var ecc = new int[count];
        foreach (var word in data)
        {
            int feedback = word ^ ecc[0];
            for (int i = 0; i < count - 1; i++) ecc[i] = ecc[i + 1];
            ecc[count - 1] = 0;
            if (feedback != 0)
                for (int i = 0; i < count; i++) ecc[i] ^= Multiply(feedback, g[i + 1]);
        }
        return ecc;
    }

    /// <summary>True when the codewords (data then error correction) form a valid Reed-Solomon codeword: used by the tests.</summary>
    public static bool IsValidCodeword(IReadOnlyList<int> all, int eccCount)
    {
        for (int root = 1; root <= eccCount; root++)
        {
            int value = 0;
            foreach (var word in all) value = Multiply(value, Exp[root]) ^ word;   // Horner: highest power first
            if (value != 0) return false;
        }
        return true;
    }

    // ---- building the symbol ----

    /// <summary>The codewords of a text, data then error correction, as the symbol carries them (for the tests).</summary>
    public static (int Size, int[] Codewords, int DataCount)? Codewords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var words = AsciiCodewords(text);
        var size = Sizes.FirstOrDefault(s => s.DataCodewords >= words.Count);
        if (size == null) return null;
        var data = Pad(words, size.DataCodewords);
        return (size.Size, data.Concat(ErrorCorrection(data, size.EccCodewords)).ToArray(), data.Count);
    }

    private static bool[][] Build(SymbolSize size, List<int> data)
    {
        var all = data.Concat(ErrorCorrection(data, size.EccCodewords)).ToList();
        var bits = Place(all, size.DataSide, size.DataSide);

        int s = size.Size, rd = size.RegionData, rc = size.RegionsPerSide;
        var grid = new bool[s][];
        for (int i = 0; i < s; i++) grid[i] = new bool[s];

        for (int ry = 0; ry < rc; ry++)
            for (int rx = 0; rx < rc; rx++)
            {
                int top = ry * (rd + 2), left = rx * (rd + 2);
                for (int i = 0; i < rd + 2; i++)
                {
                    grid[top][left + i] = i % 2 == 0;           // top edge: alternating, dark first
                    grid[top + rd + 1][left + i] = true;        // bottom edge: solid
                    grid[top + i][left] = true;                 // left edge: solid
                    grid[top + i][left + rd + 1] = i % 2 == 1;  // right edge: alternating, dark on odd rows
                }
                for (int y = 0; y < rd; y++)
                    for (int x = 0; x < rd; x++)
                        grid[top + 1 + y][left + 1 + x] = bits[(ry * rd + y) * size.DataSide + rx * rd + x] > 0;
            }
        return grid;
    }

    // ---- module placement (ISO/IEC 16022 Annex F): the codewords wind through the data area in diagonal strokes ----

    private static int[] Place(IReadOnlyList<int> codewords, int rows, int cols)
    {
        var bits = new int[rows * cols];
        Array.Fill(bits, -1);

        void Module(int row, int col, int pos, int bit)
        {
            if (row < 0) { row += rows; col += 4 - (rows + 4) % 8; }
            if (col < 0) { col += cols; row += 4 - (cols + 4) % 8; }
            bits[row * cols + col] = (codewords[pos] & (1 << (8 - bit))) != 0 ? 1 : 0;
        }

        void Utah(int row, int col, int pos)
        {
            Module(row - 2, col - 2, pos, 1);
            Module(row - 2, col - 1, pos, 2);
            Module(row - 1, col - 2, pos, 3);
            Module(row - 1, col - 1, pos, 4);
            Module(row - 1, col, pos, 5);
            Module(row, col - 2, pos, 6);
            Module(row, col - 1, pos, 7);
            Module(row, col, pos, 8);
        }

        void Corner1(int pos)
        {
            Module(rows - 1, 0, pos, 1); Module(rows - 1, 1, pos, 2); Module(rows - 1, 2, pos, 3);
            Module(0, cols - 2, pos, 4); Module(0, cols - 1, pos, 5); Module(1, cols - 1, pos, 6);
            Module(2, cols - 1, pos, 7); Module(3, cols - 1, pos, 8);
        }
        void Corner2(int pos)
        {
            Module(rows - 3, 0, pos, 1); Module(rows - 2, 0, pos, 2); Module(rows - 1, 0, pos, 3);
            Module(0, cols - 4, pos, 4); Module(0, cols - 3, pos, 5); Module(0, cols - 2, pos, 6);
            Module(0, cols - 1, pos, 7); Module(1, cols - 1, pos, 8);
        }
        void Corner3(int pos)
        {
            Module(rows - 3, 0, pos, 1); Module(rows - 2, 0, pos, 2); Module(rows - 1, 0, pos, 3);
            Module(0, cols - 2, pos, 4); Module(0, cols - 1, pos, 5); Module(1, cols - 1, pos, 6);
            Module(2, cols - 1, pos, 7); Module(3, cols - 1, pos, 8);
        }
        void Corner4(int pos)
        {
            Module(rows - 1, 0, pos, 1); Module(rows - 1, cols - 1, pos, 2); Module(0, cols - 3, pos, 3);
            Module(0, cols - 2, pos, 4); Module(0, cols - 1, pos, 5); Module(1, cols - 3, pos, 6);
            Module(1, cols - 2, pos, 7); Module(1, cols - 1, pos, 8);
        }

        int position = 0, r = 4, c = 0;
        do
        {
            // the special corner patterns come first when the sweep reaches them
            if (r == rows && c == 0) Corner1(position++);
            if (r == rows - 2 && c == 0 && cols % 4 != 0) Corner2(position++);
            if (r == rows - 2 && c == 0 && cols % 8 == 4) Corner3(position++);
            if (r == rows + 4 && c == 2 && cols % 8 == 0) Corner4(position++);

            // sweep up and to the right
            do
            {
                if (r < rows && c >= 0 && bits[r * cols + c] < 0) Utah(r, c, position++);
                r -= 2; c += 2;
            } while (r >= 0 && c < cols);
            r++; c += 3;

            // sweep down and to the left
            do
            {
                if (r >= 0 && c < cols && bits[r * cols + c] < 0) Utah(r, c, position++);
                r += 2; c -= 2;
            } while (r < rows && c >= 0);
            r += 3; c++;
        } while (r < rows || c < cols);

        // when the bottom right corner was not reached, it holds a fixed pattern
        if (bits[rows * cols - 1] < 0)
        {
            bits[rows * cols - 1] = 1;
            bits[(rows - 1) * cols - 2] = 1;
        }
        return bits;
    }
}
