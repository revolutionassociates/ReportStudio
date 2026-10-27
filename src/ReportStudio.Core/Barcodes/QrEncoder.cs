using System.Text;

namespace ReportStudio.Core.Barcodes;

/// <summary>Error correction level: how much damage a QR code survives (L ~7%, M ~15%, Q ~25%, H ~30%).</summary>
public enum QrLevel { Low, Medium, Quartile, High }

/// <summary>
/// QR Code encoder (model 2): byte mode (the text is written as UTF-8), versions 1 to 40, levels L/M/Q/H,
/// the best of the 8 masks chosen by the standard penalty rules. No dependencies.
/// </summary>
public static class QrEncoder
{
    /// <summary>Quiet zone kept clear on each side, in modules.</summary>
    public const int QuietModules = 4;

    // Indexed [level][version]; index 0 of each row is unused. Levels in the order Low, Medium, Quartile, High.
    private static readonly int[][] EccPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 }
    };

    private static readonly int[][] NumBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 }
    };

    private static readonly int[] FormatBits = { 1, 0, 3, 2 };   // L, M, Q, H as the standard numbers them

    /// <summary>Encodes the text. Rows are returned top to bottom, each row left to right; true is a dark module.</summary>
    public static bool TryEncode(string? text, QrLevel level, out bool[][] modules, out string error)
    {
        modules = Array.Empty<bool[]>();
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "there is no value to encode"; return false; }

        var data = Encoding.UTF8.GetBytes(text);
        int ecl = (int)level;

        int version = 0;
        for (int v = 1; v <= 40; v++)
        {
            int capacityBits = DataCodewords(v, ecl) * 8;
            int countBits = v <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= capacityBits) { version = v; break; }
        }
        if (version == 0)
        {
            int max = (DataCodewords(40, ecl) * 8 - 20) / 8;
            error = $"the value is too long for a QR code ({data.Length} bytes; at this error-correction level the limit is {max})";
            return false;
        }

        var codewords = BuildCodewords(data, version, ecl);
        var all = AddEccAndInterleave(codewords, version, ecl);

        var matrix = new Matrix(version, ecl);
        matrix.DrawFunctionPatterns();
        matrix.DrawCodewords(all);

        int bestMask = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            matrix.ApplyMask(mask);
            matrix.DrawFormatBits(mask);
            int penalty = matrix.Penalty();
            if (penalty < bestPenalty) { bestMask = mask; bestPenalty = penalty; }
            matrix.ApplyMask(mask);   // undo
        }
        matrix.ApplyMask(bestMask);
        matrix.DrawFormatBits(bestMask);

        modules = matrix.Modules;
        return true;
    }

    // ---- data -----------------------------------------------------------------------------------

    private static int RawModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int n = ver / 7 + 2;
            result -= (25 * n - 10) * n - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int ver, int ecl) => RawModules(ver) / 8 - EccPerBlock[ecl][ver] * NumBlocks[ecl][ver];

    private static List<int> BuildCodewords(byte[] data, int version, int ecl)
    {
        int capacityBits = DataCodewords(version, ecl) * 8;
        var bits = new List<bool>();
        void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }

        Put(0b0100, 4);                              // byte mode
        Put(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, capacityBits - bits.Count));   // terminator
        Put(0, (8 - bits.Count % 8) % 8);                  // byte align
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Put(pad, 8);

        var codewords = new List<int>();
        for (int i = 0; i < bits.Count; i += 8)
        {
            int value = 0;
            for (int j = 0; j < 8; j++) value = (value << 1) | (bits[i + j] ? 1 : 0);
            codewords.Add(value);
        }
        return codewords;
    }

    private static int[] AddEccAndInterleave(List<int> data, int ver, int ecl)
    {
        int blockCount = NumBlocks[ecl][ver], eccLen = EccPerBlock[ecl][ver];
        int raw = RawModules(ver) / 8;
        int shortBlocks = blockCount - raw % blockCount;
        int shortLen = raw / blockCount;

        var divisor = RsDivisor(eccLen);
        var blocks = new List<int[]>();
        int k = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int dataLen = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var dat = data.GetRange(k, dataLen).ToArray();
            k += dataLen;
            var ecc = RsRemainder(dat, divisor);

            var block = new int[shortLen + 1];       // short blocks keep one unused slot so all blocks line up
            Array.Copy(dat, block, dataLen);
            Array.Copy(ecc, 0, block, shortLen + 1 - eccLen, eccLen);
            blocks.Add(block);
        }

        var result = new List<int>();
        for (int i = 0; i < shortLen + 1; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= shortBlocks) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    // ---- Reed-Solomon over GF(256), polynomial 0x11D --------------------------------------------

    private static int RsMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private static int[] RsDivisor(int degree)
    {
        var result = new int[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = RsMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = RsMultiply(root, 2);
        }
        return result;
    }

    private static int[] RsRemainder(int[] data, int[] divisor)
    {
        var result = new int[divisor.Length];
        foreach (var b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < divisor.Length; i++) result[i] ^= RsMultiply(divisor[i], factor);
        }
        return result;
    }

    // ---- the module grid ------------------------------------------------------------------------

    private sealed class Matrix
    {
        private readonly int _version, _ecl, _size;
        private readonly bool[][] _m, _function;

        public Matrix(int version, int ecl)
        {
            _version = version;
            _ecl = ecl;
            _size = version * 4 + 17;
            _m = new bool[_size][];
            _function = new bool[_size][];
            for (int y = 0; y < _size; y++) { _m[y] = new bool[_size]; _function[y] = new bool[_size]; }
        }

        public bool[][] Modules => _m;

        private void SetFunction(int x, int y, bool dark)
        {
            _m[y][x] = dark;
            _function[y][x] = true;
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

        private int[] AlignmentPositions()
        {
            if (_version == 1) return Array.Empty<int>();
            int n = _version / 7 + 2;
            int step = _version == 32 ? 26 : (_version * 4 + n * 2 + 1) / (n * 2 - 2) * 2;
            var result = new int[n];
            result[0] = 6;
            for (int i = n - 1, pos = _size - 7; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        public void DrawFunctionPatterns()
        {
            for (int i = 0; i < _size; i++)
            {
                SetFunction(6, i, i % 2 == 0);   // timing patterns
                SetFunction(i, 6, i % 2 == 0);
            }

            DrawFinder(3, 3);
            DrawFinder(_size - 4, 3);
            DrawFinder(3, _size - 4);

            var pos = AlignmentPositions();
            int n = pos.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                        DrawAlignment(pos[i], pos[j]);

            DrawFormatBits(0);   // reserves the area; rewritten once the mask is chosen
            DrawVersion();
        }

        private void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= _size || y < 0 || y >= _size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(x, y, dist != 2 && dist != 4);
                }
        }

        private void DrawAlignment(int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        public void DrawFormatBits(int mask)
        {
            int data = FormatBits[_ecl] << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
            SetFunction(8, 7, Bit(bits, 6));
            SetFunction(8, 8, Bit(bits, 7));
            SetFunction(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) SetFunction(_size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetFunction(8, _size - 15 + i, Bit(bits, i));
            SetFunction(8, _size - 8, true);   // the always-dark module
        }

        private void DrawVersion()
        {
            if (_version < 7) return;
            int rem = _version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = _version << 12 | rem;

            for (int i = 0; i < 18; i++)
            {
                bool dark = Bit(bits, i);
                int a = _size - 11 + i % 3, b = i / 3;
                SetFunction(a, b, dark);
                SetFunction(b, a, dark);
            }
        }

        public void DrawCodewords(int[] data)
        {
            int i = 0;
            for (int right = _size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < _size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? _size - 1 - vert : vert;
                        if (!_function[y][x] && i < data.Length * 8)
                        {
                            _m[y][x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
                }
            }
        }

        public void ApplyMask(int mask)
        {
            for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                {
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                    };
                    if (invert && !_function[y][x]) _m[y][x] = !_m[y][x];
                }
        }

        // ---- mask penalty (runs, finder-like patterns, 2x2 blocks, dark/light balance) ----

        public int Penalty()
        {
            int result = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                bool transpose = pass == 1;
                for (int a = 0; a < _size; a++)
                {
                    bool color = false;
                    int run = 0;
                    var history = new int[7];
                    for (int b = 0; b < _size; b++)
                    {
                        bool v = transpose ? _m[b][a] : _m[a][b];
                        if (v == color)
                        {
                            run++;
                            if (run == 5) result += 3;
                            else if (run > 5) result++;
                        }
                        else
                        {
                            AddHistory(run, history);
                            if (!color) result += CountPatterns(history) * 40;
                            color = v;
                            run = 1;
                        }
                    }
                    result += TerminateAndCount(color, run, history) * 40;
                }
            }

            for (int y = 0; y < _size - 1; y++)
                for (int x = 0; x < _size - 1; x++)
                {
                    bool c = _m[y][x];
                    if (c == _m[y][x + 1] && c == _m[y + 1][x] && c == _m[y + 1][x + 1]) result += 3;
                }

            int dark = 0;
            foreach (var row in _m) foreach (var v in row) if (v) dark++;
            int total = _size * _size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return result + k * 10;
        }

        private void AddHistory(int run, int[] history)
        {
            if (history[0] == 0) run += _size;   // the light border counts toward the first run
            Array.Copy(history, 0, history, 1, 6);
            history[0] = run;
        }

        private static int CountPatterns(int[] h)
        {
            int n = h[1];
            bool core = n > 0 && h[2] == n && h[3] == n * 3 && h[4] == n && h[5] == n;
            return (core && h[0] >= n * 4 && h[6] >= n ? 1 : 0) + (core && h[6] >= n * 4 && h[0] >= n ? 1 : 0);
        }

        private int TerminateAndCount(bool color, int run, int[] history)
        {
            if (color)
            {
                AddHistory(run, history);
                run = 0;
            }
            run += _size;
            AddHistory(run, history);
            return CountPatterns(history);
        }
    }
}

/// <summary>Turns a QR code into dark rectangles that fill a box (square modules, centred, quiet zone included).</summary>
public static class QrLayout
{
    /// <summary>Dark runs as (x, y, width, height) from the box's top-left. Empty if the value cannot be encoded.</summary>
    public static IReadOnlyList<(double X, double Y, double Width, double Height)> Rectangles(
        string? text, QrLevel level, double width, double height)
    {
        if (width <= 0 || height <= 0 || !QrEncoder.TryEncode(text, level, out var modules, out _))
            return Array.Empty<(double, double, double, double)>();

        int size = modules.Length;
        double module = Math.Min(width, height) / (size + 2 * QrEncoder.QuietModules);
        double left = (width - module * size) / 2;
        double top = (height - module * size) / 2;

        var rects = new List<(double, double, double, double)>();
        for (int y = 0; y < size; y++)
        {
            int x = 0;
            while (x < size)
            {
                if (!modules[y][x]) { x++; continue; }
                int start = x;
                while (x < size && modules[y][x]) x++;      // one rectangle per horizontal run keeps the output small
                rects.Add((left + start * module, top + y * module, (x - start) * module, module));
            }
        }
        return rects;
    }
}
