using System.Numerics;
using System.Text;

namespace ReportStudio.Core.Barcodes;

/// <summary>
/// PDF417 encoder (ISO/IEC 15438): a stacked code of 3 to 90 rows and 2 to 30 data columns that holds up to about 1100
/// characters of text, 1800 bytes or 2700 digits (here at most 928 codewords per symbol, error correction included).
/// Text, byte and numeric compaction are chosen automatically as the value changes; text above ISO 8859-1 is written as
/// UTF-8 with an ECI header. The error correction level follows the standard's recommendation for the size of the value.
/// </summary>
public static class Pdf417Encoder
{
    public const int MinColumns = 2;
    public const int MaxColumns = 30;
    public const int MinRows = 3;
    public const int MaxRows = 90;
    /// <summary>The most codewords one symbol holds: length descriptor, data, padding and error correction together.</summary>
    public const int MaxSymbolCodewords = 928;
    /// <summary>How many module widths tall each row is.</summary>
    public const int RowHeight = 3;

    private const double PreferredRatio = 3.0;     // width : height the column count is chosen for
    private const int StartPattern = 0x1fea8;      // 17 modules
    private const int StopPattern = 0x3fa29;       // 18 modules

    private const int TextMode = 0, ByteMode = 1, NumericMode = 2;
    private const int Alpha = 0, Lower = 1, Mixed = 2, Punctuation = 3;

    private const int LatchText = 900, LatchBytePadded = 901, LatchNumeric = 902, ShiftByte = 913, LatchByte = 924, EciCharset = 927;

    private static readonly int[] MixedRaw =
    {
        48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 38, 13, 9, 44, 58,
        35, 45, 46, 36, 47, 43, 37, 42, 61, 94, 0, 32, 0, 0, 0
    };

    private static readonly int[] PunctuationRaw =
    {
        59, 60, 62, 64, 91, 92, 93, 95, 96, 126, 33, 13, 9, 44, 58,
        10, 45, 46, 36, 47, 34, 124, 42, 40, 41, 63, 123, 125, 39, 0
    };

    private static readonly int[] MixedCode = Invert(MixedRaw);
    private static readonly int[] PunctuationCode = Invert(PunctuationRaw);

    private static int[] Invert(int[] raw)
    {
        var table = new int[128];
        Array.Fill(table, -1);
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] > 0) table[raw[i]] = i;
        return table;
    }

    /// <summary>What the encoder worked out for a value.</summary>
    /// <param name="Columns">Data columns.</param>
    /// <param name="Rows">Rows.</param>
    /// <param name="Level">Error correction level 0 to 8.</param>
    /// <param name="SourceCount">Codewords the value itself takes.</param>
    /// <param name="Codewords">All codewords in reading order: length descriptor, data, padding, error correction.</param>
    public sealed record Plan(int Columns, int Rows, int Level, int SourceCount, int[] Codewords);

    /// <summary>Encodes the text. Returns false with a readable reason when it is empty or does not fit.</summary>
    public static bool TryEncode(string? text, out bool[][] modules, out string error)
    {
        modules = Array.Empty<bool[]>();
        var plan = MakePlan(text, out error);
        if (plan == null) return false;
        modules = Draw(plan);
        return true;
    }

    /// <summary>Works out the codewords and the shape of the symbol, or null with the reason.</summary>
    public static Plan? MakePlan(string? text, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "there is no value to encode"; return null; }

        var words = HighLevel(text);
        int m = words.Count;
        int level = RecommendedLevel(m);
        if (level < 0 || m + 1 + (2 << level) > MaxSymbolCodewords)
        {
            error = $"the value is too long for a PDF417 (it needs {m} codewords, at most 863 fit; digits take less room than other characters)";
            return null;
        }
        int k = 2 << level;                                  // 2^(level + 1) error correction codewords

        // the number of columns that makes the symbol closest to the preferred shape
        int bestColumns = 0, bestRows = 0;
        double ratio = 0;
        for (int c = MinColumns; c <= MaxColumns; c++)
        {
            int r = RowsFor(m, k, c);
            if (r < MinRows) break;
            if (r > MaxRows) continue;
            double shape = (17.0 * c + 69) / (r * RowHeight);
            if (bestColumns != 0 && Math.Abs(shape - PreferredRatio) > Math.Abs(ratio - PreferredRatio)) continue;
            ratio = shape;
            bestColumns = c;
            bestRows = r;
        }
        if (bestColumns == 0) { error = "the value does not fit any PDF417 shape"; return null; }

        int pad = Math.Max(0, bestColumns * bestRows - k - m - 1);
        var data = new List<int>(m + pad + 1) { m + pad + 1 };
        data.AddRange(words);
        for (int i = 0; i < pad; i++) data.Add(LatchText);   // 900 doubles as the padding codeword

        var all = new int[data.Count + k];
        data.CopyTo(all);
        ErrorCorrection(data, level).CopyTo(all, data.Count);
        return new Plan(bestColumns, bestRows, level, m, all);
    }

    /// <summary>The rows needed for m source codewords and k error correction codewords in c columns (annex Q).</summary>
    private static int RowsFor(int m, int k, int c)
    {
        int r = (m + 1 + k) / c + 1;
        if (c * r >= m + 1 + k + c) r--;
        return r;
    }

    /// <summary>The error correction level the standard recommends for a value of this many codewords, or -1 if none fits.</summary>
    public static int RecommendedLevel(int sourceCodewords) =>
        sourceCodewords <= 40 ? 2 : sourceCodewords <= 160 ? 3 : sourceCodewords <= 320 ? 4 : sourceCodewords <= 863 ? 5 : -1;

    // ---- drawing ----

    private static bool[][] Draw(Plan plan)
    {
        int c = plan.Columns, r = plan.Rows, level = plan.Level;
        var grid = new bool[r][];
        int index = 0;
        for (int y = 0; y < r; y++)
        {
            int cluster = y % 3;
            int left, right;
            if (cluster == 0)
            {
                left = 30 * (y / 3) + (r - 1) / 3;
                right = 30 * (y / 3) + (c - 1);
            }
            else if (cluster == 1)
            {
                left = 30 * (y / 3) + level * 3 + (r - 1) % 3;
                right = 30 * (y / 3) + (r - 1) / 3;
            }
            else
            {
                left = 30 * (y / 3) + (c - 1);
                right = 30 * (y / 3) + level * 3 + (r - 1) % 3;
            }

            var table = Pdf417Tables.Clusters[cluster];
            var row = new bool[17 * c + 69];
            int x = 0;
            Put(row, ref x, StartPattern, 17);
            Put(row, ref x, table[left], 17);
            for (int i = 0; i < c; i++) Put(row, ref x, table[plan.Codewords[index++]], 17);
            Put(row, ref x, table[right], 17);
            Put(row, ref x, StopPattern, 18);
            grid[y] = row;
        }
        return grid;
    }

    private static void Put(bool[] row, ref int x, int pattern, int length)
    {
        for (int i = 0; i < length; i++)
            row[x++] = ((pattern >> (length - 1 - i)) & 1) != 0;
    }

    // ---- error correction over GF(929): the generator is the product of (x - 3^i) for i = 1..k ----

    /// <summary>The k = 2^(level+1) error correction codewords for the data codewords (length descriptor included).</summary>
    public static int[] ErrorCorrection(IReadOnlyList<int> data, int level)
    {
        int k = 2 << level;
        var coefficient = Generator(k);
        var e = new int[k];
        foreach (int word in data)
        {
            int t1 = (word + e[k - 1]) % 929;
            for (int j = k - 1; j >= 1; j--)
                e[j] = (e[j - 1] + 929 - t1 * coefficient[j] % 929) % 929;
            e[0] = (929 - t1 * coefficient[0] % 929) % 929;
        }

        var result = new int[k];
        for (int j = k - 1, i = 0; j >= 0; j--, i++)
            result[i] = e[j] == 0 ? 0 : 929 - e[j];
        return result;
    }

    /// <summary>The generator polynomial's coefficients from the lowest power up, without the leading 1.</summary>
    private static int[] Generator(int k)
    {
        var g = new int[k + 1];
        g[0] = 1;
        int length = 1, root = 1;
        for (int i = 1; i <= k; i++)
        {
            root = root * 3 % 929;                           // 3^i
            var next = new int[k + 1];
            for (int j = 0; j < length; j++)
            {
                next[j + 1] = (next[j + 1] + g[j]) % 929;
                next[j] = (next[j] - g[j] * root % 929 + 929) % 929;
            }
            g = next;
            length++;
        }
        return g;
    }

    // ---- high-level encoding: text, byte and numeric compaction ----

    /// <summary>The codewords for the value, before the length descriptor, padding and error correction.</summary>
    public static List<int> HighLevel(string text)
    {
        var output = new List<int>();
        byte[] s;
        if (text.All(ch => ch <= 255)) s = Encoding.Latin1.GetBytes(text);
        else
        {
            s = Encoding.UTF8.GetBytes(text);
            output.Add(EciCharset);                          // ECI 26 = UTF-8
            output.Add(26);
        }

        int mode = TextMode, submode = Alpha, p = 0;
        while (p < s.Length)
        {
            int digits = DigitRun(s, p);
            if (digits >= 13)
            {
                output.Add(LatchNumeric);
                mode = NumericMode;
                submode = Alpha;
                EncodeNumeric(s, p, digits, output);
                p += digits;
                continue;
            }

            int textRun = TextRun(s, p);
            if (textRun >= 5 || digits == s.Length || (textRun > 0 && p + textRun == s.Length))
            {
                if (mode != TextMode)
                {
                    output.Add(LatchText);
                    mode = TextMode;
                    submode = Alpha;
                }
                submode = EncodeText(s, p, textRun, output, submode);
                p += textRun;
            }
            else
            {
                int count = BinaryRun(s, p);
                if (count == 1 && mode == TextMode)
                    EncodeBinary(s, p, 1, TextMode, output);         // one byte: a shift, no latch
                else
                {
                    EncodeBinary(s, p, count, mode, output);
                    mode = ByteMode;
                    submode = Alpha;
                }
                p += count;
            }
        }
        return output;
    }

    private static bool IsDigit(int b) => b >= '0' && b <= '9';
    private static bool IsUpper(int b) => b == ' ' || (b >= 'A' && b <= 'Z');
    private static bool IsLower(int b) => b == ' ' || (b >= 'a' && b <= 'z');
    private static bool IsMixed(int b) => b < 128 && MixedCode[b] != -1;
    private static bool IsPunctuation(int b) => b < 128 && PunctuationCode[b] != -1;
    private static bool IsText(int b) => b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126);

    private static int DigitRun(byte[] s, int p)
    {
        int n = 0;
        while (p + n < s.Length && IsDigit(s[p + n])) n++;
        return n;
    }

    /// <summary>How many bytes from p can be written in text compaction, stopping where a long run of digits begins.</summary>
    private static int TextRun(byte[] s, int p)
    {
        int idx = p;
        while (idx < s.Length)
        {
            int numeric = 0;
            while (numeric < 13 && idx < s.Length && IsDigit(s[idx]))
            {
                numeric++;
                idx++;
            }
            if (numeric >= 13) return idx - p - numeric;
            if (numeric > 0) continue;                       // a few digits are fine inside text
            if (!IsText(s[idx])) break;
            idx++;
        }
        return idx - p;
    }

    /// <summary>How many bytes from p go into one byte-compaction run: up to a long run of digits or a stretch of real text.</summary>
    private static int BinaryRun(byte[] s, int p)
    {
        int count = 1;
        while (p + count < s.Length && DigitRun(s, p + count) < 13 && TextRun(s, p + count) < 5) count++;
        return count;
    }

    /// <summary>Text compaction of s[start .. start+count): two characters per codeword. Returns the sub-mode it ends in.</summary>
    private static int EncodeText(byte[] s, int start, int count, List<int> output, int submode)
    {
        var tmp = new List<int>(count + 4);
        int idx = 0;
        while (true)
        {
            int ch = s[start + idx];
            bool consumed = true;                            // false when only a sub-mode change was written
            switch (submode)
            {
                case Alpha:
                    if (IsUpper(ch)) tmp.Add(ch == ' ' ? 26 : ch - 'A');
                    else if (IsLower(ch)) { submode = Lower; tmp.Add(27); consumed = false; }
                    else if (IsMixed(ch)) { submode = Mixed; tmp.Add(28); consumed = false; }
                    else { tmp.Add(29); tmp.Add(PunctuationCode[ch]); }
                    break;
                case Lower:
                    if (IsLower(ch)) tmp.Add(ch == ' ' ? 26 : ch - 'a');
                    else if (IsUpper(ch)) { tmp.Add(27); tmp.Add(ch - 'A'); }
                    else if (IsMixed(ch)) { submode = Mixed; tmp.Add(28); consumed = false; }
                    else { tmp.Add(29); tmp.Add(PunctuationCode[ch]); }
                    break;
                case Mixed:
                    if (IsMixed(ch)) tmp.Add(MixedCode[ch]);
                    else if (IsUpper(ch)) { submode = Alpha; tmp.Add(28); consumed = false; }
                    else if (IsLower(ch)) { submode = Lower; tmp.Add(27); consumed = false; }
                    else if (idx + 1 < count && IsPunctuation(s[start + idx + 1])) { submode = Punctuation; tmp.Add(25); consumed = false; }
                    else { tmp.Add(29); tmp.Add(PunctuationCode[ch]); }
                    break;
                default:
                    if (IsPunctuation(ch)) tmp.Add(PunctuationCode[ch]);
                    else { submode = Alpha; tmp.Add(29); consumed = false; }
                    break;
            }
            if (consumed)
            {
                idx++;
                if (idx >= count) break;
            }
        }

        int h = 0;
        for (int i = 0; i < tmp.Count; i++)
        {
            if (i % 2 == 1)
            {
                h = h * 30 + tmp[i];
                output.Add(h);
            }
            else h = tmp[i];
        }
        if (tmp.Count % 2 == 1) output.Add(h * 30 + 29);     // the odd one out is padded with "punctuation shift"
        return submode;
    }

    /// <summary>Byte compaction: six bytes become five codewords, the rest are written one for one.</summary>
    private static void EncodeBinary(byte[] s, int start, int count, int startMode, List<int> output)
    {
        if (count == 1 && startMode == TextMode) output.Add(ShiftByte);
        else output.Add(count % 6 == 0 ? LatchByte : LatchBytePadded);

        int idx = start;
        while (start + count - idx >= 6)
        {
            long t = 0;
            for (int i = 0; i < 6; i++) t = (t << 8) + s[idx + i];
            var chars = new int[5];
            for (int i = 0; i < 5; i++)
            {
                chars[i] = (int)(t % 900);
                t /= 900;
            }
            for (int i = 4; i >= 0; i--) output.Add(chars[i]);
            idx += 6;
        }
        for (int i = idx; i < start + count; i++) output.Add(s[i]);
    }

    /// <summary>Numeric compaction: up to 44 digits at a time become a number written in base 900.</summary>
    private static void EncodeNumeric(byte[] s, int start, int count, List<int> output)
    {
        int idx = 0;
        var tmp = new List<int>();
        while (idx < count)
        {
            int length = Math.Min(44, count - idx);
            var number = BigInteger.Parse("1" + Encoding.ASCII.GetString(s, start + idx, length), System.Globalization.CultureInfo.InvariantCulture);
            tmp.Clear();
            do
            {
                tmp.Add((int)(number % 900));
                number /= 900;
            } while (!number.IsZero);
            for (int i = tmp.Count - 1; i >= 0; i--) output.Add(tmp[i]);
            idx += length;
        }
    }
}
