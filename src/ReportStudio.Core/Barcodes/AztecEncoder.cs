using System.Text;

namespace ReportStudio.Core.Barcodes;

/// <summary>
/// Aztec Code encoder (ISO/IEC 24778): a square code with a bull's-eye in the middle, so it can be read without a quiet zone
/// and from any angle. Compact symbols (15 to 27 modules) and full symbols (19 to 151 modules, with reference lines every 16
/// modules) are chosen by size, with 33 % error correction. Text, digit, mixed and punctuation sub-modes and binary shifts are
/// picked by a shortest-path search; text above ISO 8859-1 is written as UTF-8 with an ECI header.
/// About 3000 letters, 3700 digits or 1800 other characters fit in the largest symbol.
/// </summary>
public static class AztecEncoder
{
    public const int MaxLayers = 32;
    private const int EccPercent = 33;

    private const int Upper = 0, Lower = 1, Digit = 2, Mixed = 3, Punct = 4;

    /// <summary>The cheapest way from one sub-mode to another: bit count in the high half, the bits in the low half.</summary>
    private static readonly int[][] LatchTable =
    {
        new[] { 0, (5 << 16) + 28, (5 << 16) + 30, (5 << 16) + 29, (10 << 16) + (29 << 5) + 30 },
        new[] { (9 << 16) + (30 << 4) + 14, 0, (5 << 16) + 30, (5 << 16) + 29, (10 << 16) + (29 << 5) + 30 },
        new[] { (4 << 16) + 14, (9 << 16) + (14 << 5) + 28, 0, (9 << 16) + (14 << 5) + 29, (14 << 16) + (14 << 10) + (29 << 5) + 30 },
        new[] { (5 << 16) + 29, (5 << 16) + 28, (10 << 16) + (29 << 5) + 30, 0, (5 << 16) + 30 },
        new[] { (5 << 16) + 31, (10 << 16) + (31 << 5) + 28, (10 << 16) + (31 << 5) + 30, (10 << 16) + (31 << 5) + 29, 0 },
    };

    private static readonly int[] MixedChars =
    {
        0, 32, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 27, 28, 29, 30, 31, 64, 92, 94, 95, 96, 124, 126, 127
    };

    private static readonly int[] PunctChars =
    {
        0, 13, 0, 0, 0, 0, 33, 39, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 58, 59, 60, 61, 62, 63, 91, 93, 123, 125
    };

    /// <summary>For each sub-mode and byte, its code there (0 = not available).</summary>
    private static readonly int[][] CharMap = BuildCharMap();

    /// <summary>The shift codes: from a sub-mode, the code that borrows a character from another one (-1 = none).</summary>
    private static readonly int[][] ShiftTable = BuildShiftTable();

    /// <summary>Bits per codeword for each layer count 0 to 32.</summary>
    private static readonly int[] WordSizes =
    {
        4, 6, 6, 8, 8, 8, 8, 8, 8, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12
    };

    private static int[][] BuildCharMap()
    {
        var map = new int[5][];
        for (int i = 0; i < 5; i++) map[i] = new int[256];
        map[Upper][' '] = 1;
        for (int c = 'A'; c <= 'Z'; c++) map[Upper][c] = c - 'A' + 2;
        map[Lower][' '] = 1;
        for (int c = 'a'; c <= 'z'; c++) map[Lower][c] = c - 'a' + 2;
        map[Digit][' '] = 1;
        for (int c = '0'; c <= '9'; c++) map[Digit][c] = c - '0' + 2;
        map[Digit][','] = 12;
        map[Digit]['.'] = 13;
        for (int i = 0; i < MixedChars.Length; i++) map[Mixed][MixedChars[i]] = i;
        for (int i = 0; i < PunctChars.Length; i++)
            if (PunctChars[i] > 0) map[Punct][PunctChars[i]] = i;
        return map;
    }

    private static int[][] BuildShiftTable()
    {
        var table = new int[5][];
        for (int i = 0; i < 5; i++)
        {
            table[i] = new int[5];
            Array.Fill(table[i], -1);
        }
        table[Upper][Punct] = 0;
        table[Lower][Punct] = 0;
        table[Lower][Upper] = 28;
        table[Mixed][Punct] = 0;
        table[Digit][Punct] = 0;
        table[Digit][Upper] = 15;
        return table;
    }

    /// <summary>The symbol and how it was built.</summary>
    /// <param name="Modules">Rows of modules, true = dark.</param>
    /// <param name="Compact">A compact symbol (one bull's-eye ring fewer, no reference lines).</param>
    /// <param name="Layers">Layers of data around the bull's-eye.</param>
    /// <param name="WordSize">Bits per codeword: 4 to 12.</param>
    /// <param name="DataWords">Codewords that carry the value; the rest are error correction.</param>
    public sealed record Result(bool[][] Modules, bool Compact, int Layers, int WordSize, int DataWords)
    {
        public int Size => Modules.Length;
    }

    /// <summary>Encodes the text. Returns false with a readable reason when it is empty or does not fit.</summary>
    public static bool TryEncode(string? text, out bool[][] modules, out string error)
    {
        modules = Array.Empty<bool[]>();
        var result = Encode(text, out error);
        if (result == null) return false;
        modules = result.Modules;
        return true;
    }

    /// <summary>Encodes the text, or returns null with the reason.</summary>
    public static Result? Encode(string? text, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "there is no value to encode"; return null; }

        var (data, eci) = ToBytes(text);
        var bits = HighLevel(data, eci);

        int eccBits = bits.Count * EccPercent / 100 + 11;
        int totalSizeBits = bits.Count + eccBits;
        bool compact = false;
        int layers = 0, totalBitsInLayer = 0, wordSize = 0;
        List<bool>? stuffed = null;
        bool found = false;
        for (int i = 0; i <= MaxLayers; i++)
        {
            // compact 1 to 4, then full 4 and up (full 1 to 3 are the same size as compact but hold less)
            compact = i <= 3;
            layers = compact ? i + 1 : i;
            totalBitsInLayer = TotalBitsInLayer(layers, compact);
            if (totalSizeBits > totalBitsInLayer) continue;

            if (stuffed == null || wordSize != WordSizes[layers])
            {
                wordSize = WordSizes[layers];
                stuffed = StuffBits(bits, wordSize);
            }
            int usable = totalBitsInLayer - totalBitsInLayer % wordSize;
            if (compact && stuffed.Count > wordSize * 64) continue;     // compact symbols hold at most 64 codewords
            if (stuffed.Count + eccBits <= usable) { found = true; break; }
        }
        if (!found || stuffed == null)
        {
            error = "the value is too long for an Aztec code (about 3000 letters, 3700 digits or 1800 other characters at most)";
            return null;
        }

        var messageBits = CheckWords(stuffed, totalBitsInLayer, wordSize);
        int messageWords = stuffed.Count / wordSize;
        var modeMessage = ModeMessage(compact, layers, messageWords);
        return new Result(Draw(compact, layers, messageBits, modeMessage), compact, layers, wordSize, messageWords);
    }

    /// <summary>The high-level bits for a value as a string of 0 and 1 (before stuffing and error correction).</summary>
    public static string HighLevelBits(string text)
    {
        var (data, eci) = ToBytes(text);
        var sb = new StringBuilder();
        foreach (bool bit in HighLevel(data, eci)) sb.Append(bit ? '1' : '0');
        return sb.ToString();
    }

    private static (byte[] Data, int Eci) ToBytes(string text)
    {
        if (text.All(ch => ch <= 255)) return (Encoding.Latin1.GetBytes(text), -1);
        return (Encoding.UTF8.GetBytes(text), 26);          // ECI 26 = UTF-8
    }

    private static int TotalBitsInLayer(int layers, bool compact) => ((compact ? 88 : 112) + 16 * layers) * layers;

    private static void AppendBits(List<bool> list, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--) list.Add(((value >> i) & 1) != 0);
    }

    // ---- high level: the shortest sequence of latches, shifts and binary shifts ----

    private sealed class Token
    {
        public readonly Token? Previous;
        public readonly int Value, Bits, BinaryStart, BinaryCount;

        public Token(Token? previous, int value, int bits, int binaryStart = 0, int binaryCount = 0)
        {
            Previous = previous;
            Value = value;
            Bits = bits;
            BinaryStart = binaryStart;
            BinaryCount = binaryCount;
        }

        public Token Add(int value, int bits) => new(this, value, bits);
        public Token AddBinaryShift(int start, int count) => new(this, 0, 0, start, count);
    }

    private sealed class State
    {
        public readonly Token Token;
        public readonly int Mode, BinaryShiftByteCount, BitCount, BinaryShiftCost;

        public State(Token token, int mode, int binaryBytes, int bitCount)
        {
            Token = token;
            Mode = mode;
            BinaryShiftByteCount = binaryBytes;
            BitCount = bitCount;
            BinaryShiftCost = binaryBytes > 62 ? 21 : binaryBytes > 31 ? 20 : binaryBytes > 0 ? 10 : 0;
        }

        /// <summary>FLG(n): the ECI header, written as a shift to punctuation, the number of digits, then the digits.</summary>
        public State AppendFlgn(int eci)
        {
            var result = ShiftAndAppend(Punct, 0);
            var token = result.Token;
            string digits = eci.ToString(System.Globalization.CultureInfo.InvariantCulture);
            token = token.Add(digits.Length, 3);
            foreach (char d in digits) token = token.Add(d - '0' + 2, 4);
            return new State(token, Mode, 0, BitCount + 3 + digits.Length * 4);
        }

        public State LatchAndAppend(int mode, int value)
        {
            int bitCount = BitCount;
            var token = Token;
            if (mode != Mode)
            {
                int latch = LatchTable[Mode][mode];
                token = token.Add(latch & 0xFFFF, latch >> 16);
                bitCount += latch >> 16;
            }
            int bits = mode == Digit ? 4 : 5;
            return new State(token.Add(value, bits), mode, 0, bitCount + bits);
        }

        public State ShiftAndAppend(int mode, int value)
        {
            int bits = Mode == Digit ? 4 : 5;
            var token = Token.Add(ShiftTable[Mode][mode], bits).Add(value, 5);
            return new State(token, Mode, 0, BitCount + bits + 5);
        }

        public State AddBinaryShiftChar(int index)
        {
            var token = Token;
            int mode = Mode, bitCount = BitCount;
            if (mode == Punct || mode == Digit)
            {
                int latch = LatchTable[mode][Upper];
                token = token.Add(latch & 0xFFFF, latch >> 16);
                bitCount += latch >> 16;
                mode = Upper;
            }
            int delta = BinaryShiftByteCount == 0 || BinaryShiftByteCount == 31 ? 18 : BinaryShiftByteCount == 62 ? 9 : 8;
            var result = new State(token, mode, BinaryShiftByteCount + 1, bitCount + delta);
            if (result.BinaryShiftByteCount == 2047 + 31) result = result.EndBinaryShift(index + 1);
            return result;
        }

        public State EndBinaryShift(int index)
        {
            if (BinaryShiftByteCount == 0) return this;
            return new State(Token.AddBinaryShift(index - BinaryShiftByteCount, BinaryShiftByteCount), Mode, 0, BitCount);
        }

        public bool IsBetterThanOrEqualTo(State other)
        {
            int newModeBitCount = BitCount + (LatchTable[Mode][other.Mode] >> 16);
            if (BinaryShiftByteCount < other.BinaryShiftByteCount)
                newModeBitCount += other.BinaryShiftCost - BinaryShiftCost;
            else if (BinaryShiftByteCount > other.BinaryShiftByteCount && other.BinaryShiftByteCount > 0)
                newModeBitCount += 10;
            return newModeBitCount <= other.BitCount;
        }

        public List<bool> ToBits(byte[] data)
        {
            var tokens = new List<Token>();
            for (Token? t = EndBinaryShift(data.Length).Token; t != null; t = t.Previous) tokens.Add(t);

            var bits = new List<bool>();
            for (int k = tokens.Count - 1; k >= 0; k--)
            {
                var t = tokens[k];
                if (t.BinaryCount == 0)
                {
                    AppendBits(bits, t.Value, t.Bits);
                    continue;
                }
                int count = t.BinaryCount;
                for (int i = 0; i < count; i++)
                {
                    if (i == 0 || (i == 31 && count <= 62))
                    {
                        // a header before the first byte, and before byte 31 when there are at most 62 bytes
                        AppendBits(bits, 31, 5);                               // binary shift
                        if (count > 62) AppendBits(bits, count - 31, 16);
                        else if (i == 0) AppendBits(bits, Math.Min(count, 31), 5);
                        else AppendBits(bits, count - 31, 5);
                    }
                    AppendBits(bits, data[t.BinaryStart + i], 8);
                }
            }
            return bits;
        }
    }

    private static List<bool> HighLevel(byte[] data, int eci)
    {
        var initial = new State(new Token(null, 0, 0), Upper, 0, 0);
        if (eci >= 0) initial = initial.AppendFlgn(eci);

        var states = new List<State> { initial };
        for (int index = 0; index < data.Length; index++)
        {
            int next = index + 1 < data.Length ? data[index + 1] : 0;
            int pair = data[index] switch
            {
                13 => next == 10 ? 2 : 0,      // CR LF
                46 => next == 32 ? 3 : 0,      // ". "
                44 => next == 32 ? 4 : 0,      // ", "
                58 => next == 32 ? 5 : 0,      // ": "
                _ => 0
            };
            if (pair > 0)
            {
                states = UpdateForPair(states, index, pair);
                index++;
            }
            else states = UpdateForChar(states, data, index);
        }

        State best = states[0];
        foreach (var s in states)
            if (s.BitCount < best.BitCount) best = s;
        return best.ToBits(data);
    }

    private static List<State> UpdateForChar(List<State> states, byte[] data, int index)
    {
        var result = new List<State>();
        int ch = data[index];
        foreach (var state in states)
        {
            bool inCurrent = CharMap[state.Mode][ch] > 0;
            State? noBinary = null;
            for (int mode = 0; mode <= Punct; mode++)
            {
                int inMode = CharMap[mode][ch];
                if (inMode <= 0) continue;
                noBinary ??= state.EndBinaryShift(index);
                // latching only helps if the character is not already available (or the mode is digits, which is cheap)
                if (!inCurrent || mode == state.Mode || mode == Digit)
                    result.Add(noBinary.LatchAndAppend(mode, inMode));
                if (!inCurrent && ShiftTable[state.Mode][mode] >= 0)
                    result.Add(noBinary.ShiftAndAppend(mode, inMode));
            }
            // a binary shift is only worth starting for a character the current mode cannot write
            if (state.BinaryShiftByteCount > 0 || CharMap[state.Mode][ch] == 0)
                result.Add(state.AddBinaryShiftChar(index));
        }
        return Simplify(result);
    }

    private static List<State> UpdateForPair(List<State> states, int index, int pair)
    {
        var result = new List<State>();
        foreach (var state in states)
        {
            var noBinary = state.EndBinaryShift(index);
            result.Add(noBinary.LatchAndAppend(Punct, pair));
            if (state.Mode != Punct) result.Add(noBinary.ShiftAndAppend(Punct, pair));
            if (pair == 3 || pair == 4)
                result.Add(noBinary.LatchAndAppend(Digit, 16 - pair).LatchAndAppend(Digit, 1));   // "." or "," then space, as digits
            if (state.BinaryShiftByteCount > 0)
                result.Add(state.AddBinaryShiftChar(index).AddBinaryShiftChar(index + 1));
        }
        return Simplify(result);
    }

    /// <summary>Drops every state that another one beats or equals.</summary>
    private static List<State> Simplify(List<State> states)
    {
        var result = new LinkedList<State>();
        foreach (var candidate in states)
        {
            bool add = true;
            for (var node = result.First; node != null;)
            {
                var following = node.Next;
                if (node.Value.IsBetterThanOrEqualTo(candidate)) { add = false; break; }
                if (candidate.IsBetterThanOrEqualTo(node.Value)) result.Remove(node);
                node = following;
            }
            if (add) result.AddFirst(candidate);
        }
        return result.ToList();
    }

    // ---- bit stuffing and error correction ----

    /// <summary>No codeword may be all zeros or all ones: such a word gets an extra bit that differs.</summary>
    private static List<bool> StuffBits(List<bool> bits, int wordSize)
    {
        var output = new List<bool>(bits.Count + bits.Count / wordSize + wordSize);
        int n = bits.Count;
        int mask = (1 << wordSize) - 2;
        for (int i = 0; i < n; i += wordSize)
        {
            int word = 0;
            for (int j = 0; j < wordSize; j++)
                if (i + j >= n || bits[i + j]) word |= 1 << (wordSize - 1 - j);

            if ((word & mask) == mask) { AppendBits(output, word & mask, wordSize); i--; }
            else if ((word & mask) == 0) { AppendBits(output, word | 1, wordSize); i--; }
            else AppendBits(output, word, wordSize);
        }
        return output;
    }

    private sealed class Field
    {
        public readonly int Size;
        public readonly int[] Exp, Log;

        public Field(int primitive, int size)
        {
            Size = size;
            Exp = new int[size];
            Log = new int[size];
            int x = 1;
            for (int i = 0; i < size; i++)
            {
                Exp[i] = x;
                x <<= 1;
                if (x >= size)
                {
                    x ^= primitive;
                    x &= size - 1;
                }
            }
            for (int i = 0; i < size - 1; i++) Log[Exp[i]] = i;
        }

        public int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : Exp[(Log[a] + Log[b]) % (Size - 1)];
    }

    private static readonly Dictionary<int, Field> Fields = new()
    {
        [4] = new Field(0x13, 16),
        [6] = new Field(0x43, 64),
        [8] = new Field(0x12D, 256),
        [10] = new Field(0x409, 1024),
        [12] = new Field(0x1069, 4096),
    };

    /// <summary>Fills the last <paramref name="ec"/> words with the Reed-Solomon check words of the words before them.</summary>
    private static void AddCheckWords(int[] words, int ec, int wordSize)
    {
        var field = Fields[wordSize];
        var g = new int[ec + 1];
        g[0] = 1;
        int length = 1;
        for (int i = 1; i <= ec; i++)
        {
            var next = new int[ec + 1];
            for (int j = 0; j < length; j++)
            {
                next[j] ^= g[j];
                next[j + 1] ^= field.Multiply(g[j], field.Exp[i]);
            }
            g = next;
            length++;
        }

        int dataCount = words.Length - ec;
        var e = new int[ec];
        for (int w = 0; w < dataCount; w++)
        {
            int feedback = words[w] ^ e[0];
            Array.Copy(e, 1, e, 0, ec - 1);
            e[ec - 1] = 0;
            if (feedback != 0)
                for (int i = 0; i < ec; i++) e[i] ^= field.Multiply(feedback, g[i + 1]);
        }
        Array.Copy(e, 0, words, dataCount, ec);
    }

    /// <summary>The message bits followed by their check words, filling totalBits; a partial leading word is zero padding.</summary>
    private static List<bool> CheckWords(List<bool> bits, int totalBits, int wordSize)
    {
        int messageWords = bits.Count / wordSize;
        int totalWords = totalBits / wordSize;
        var words = new int[totalWords];
        for (int i = 0; i < messageWords; i++)
        {
            int value = 0;
            for (int j = 0; j < wordSize; j++)
                if (bits[i * wordSize + j]) value |= 1 << (wordSize - j - 1);
            words[i] = value;
        }
        AddCheckWords(words, totalWords - messageWords, wordSize);

        var result = new List<bool>(totalBits);
        AppendBits(result, 0, totalBits % wordSize);
        foreach (int word in words) AppendBits(result, word, wordSize);
        return result;
    }

    /// <summary>The mode message: layer count and word count, with 4-bit check words, drawn around the bull's-eye.</summary>
    private static List<bool> ModeMessage(bool compact, int layers, int messageWords)
    {
        var bits = new List<bool>();
        if (compact)
        {
            AppendBits(bits, layers - 1, 2);
            AppendBits(bits, messageWords - 1, 6);
            return CheckWords(bits, 28, 4);
        }
        AppendBits(bits, layers - 1, 5);
        AppendBits(bits, messageWords - 1, 11);
        return CheckWords(bits, 40, 4);
    }

    // ---- drawing ----

    private static bool[][] Draw(bool compact, int layers, List<bool> message, List<bool> modeMessage)
    {
        int baseSize = (compact ? 11 : 14) + layers * 4;       // without reference lines
        var map = new int[baseSize];
        int size;
        if (compact)
        {
            size = baseSize;
            for (int i = 0; i < baseSize; i++) map[i] = i;
        }
        else
        {
            size = baseSize + 1 + 2 * ((baseSize / 2 - 1) / 15);
            int origCenter = baseSize / 2, center = size / 2;
            for (int i = 0; i < origCenter; i++)
            {
                int offset = i + i / 15;
                map[origCenter - i - 1] = center - offset - 1;
                map[origCenter + i] = center + offset + 1;
            }
        }

        var grid = new bool[size][];
        for (int i = 0; i < size; i++) grid[i] = new bool[size];
        void Set(int x, int y) => grid[y][x] = true;

        // the data, layer by layer from the outside in, two modules wide
        int rowOffset = 0;
        for (int i = 0; i < layers; i++)
        {
            int rowSize = (layers - i) * 4 + (compact ? 9 : 12);
            for (int j = 0; j < rowSize; j++)
            {
                int columnOffset = j * 2;
                for (int k = 0; k < 2; k++)
                {
                    if (message[rowOffset + columnOffset + k]) Set(map[i * 2 + k], map[i * 2 + j]);
                    if (message[rowOffset + rowSize * 2 + columnOffset + k]) Set(map[i * 2 + j], map[baseSize - 1 - i * 2 - k]);
                    if (message[rowOffset + rowSize * 4 + columnOffset + k]) Set(map[baseSize - 1 - i * 2 - k], map[baseSize - 1 - i * 2 - j]);
                    if (message[rowOffset + rowSize * 6 + columnOffset + k]) Set(map[baseSize - 1 - i * 2 - j], map[i * 2 + k]);
                }
            }
            rowOffset += rowSize * 8;
        }

        int c = size / 2;

        // the mode message around the bull's-eye
        if (compact)
        {
            for (int i = 0; i < 7; i++)
            {
                int offset = c - 3 + i;
                if (modeMessage[i]) Set(offset, c - 5);
                if (modeMessage[i + 7]) Set(c + 5, offset);
                if (modeMessage[20 - i]) Set(offset, c + 5);
                if (modeMessage[27 - i]) Set(c - 5, offset);
            }
        }
        else
        {
            for (int i = 0; i < 10; i++)
            {
                int offset = c - 5 + i + i / 5;
                if (modeMessage[i]) Set(offset, c - 7);
                if (modeMessage[i + 10]) Set(c + 7, offset);
                if (modeMessage[29 - i]) Set(offset, c + 7);
                if (modeMessage[39 - i]) Set(c - 7, offset);
            }
        }

        // the bull's-eye with its orientation marks, and the reference lines of a full symbol
        void BullsEye(int center, int radius)
        {
            for (int i = 0; i < radius; i += 2)
                for (int j = center - i; j <= center + i; j++)
                {
                    Set(j, center - i);
                    Set(j, center + i);
                    Set(center - i, j);
                    Set(center + i, j);
                }
            Set(center - radius, center - radius);
            Set(center - radius + 1, center - radius);
            Set(center - radius, center - radius + 1);
            Set(center + radius, center - radius);
            Set(center + radius, center - radius + 1);
            Set(center + radius, center + radius - 1);
        }

        if (compact) BullsEye(c, 5);
        else
        {
            BullsEye(c, 7);
            for (int i = 0, j = 0; i < baseSize / 2; i += 15, j += 16)
                for (int k = c & 1; k < size; k += 2)
                {
                    Set(c - j, k);
                    Set(c + j, k);
                    Set(k, c - j);
                    Set(k, c + j);
                }
        }
        return grid;
    }
}
