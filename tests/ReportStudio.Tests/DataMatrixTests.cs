using System.Globalization;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Data Matrix ECC 200 (requirement 33, first code).</summary>
public class DataMatrixTests
{
    private static bool[][] Encode(string text)
    {
        Assert.True(DataMatrixEncoder.TryEncode(text, out var modules, out var error), error);
        return modules;
    }

    // ---- encodation ----

    [Fact]
    public void Characters_become_their_code_plus_one()
    {
        Assert.Equal(new[] { 66 }, DataMatrixEncoder.AsciiCodewords("A"));
        Assert.Equal(new[] { 98, 99 }, DataMatrixEncoder.AsciiCodewords("ab"));
    }

    [Fact]
    public void Digit_pairs_share_one_codeword()
    {
        Assert.Equal(new[] { 142, 164, 186 }, DataMatrixEncoder.AsciiCodewords("123456"));
        // an odd digit at the end stands alone: "5" is 53 + 1
        Assert.Equal(new[] { 142, 164, 54 }, DataMatrixEncoder.AsciiCodewords("12345"));
    }

    [Fact]
    public void A_letter_between_digits_breaks_the_pairing()
    {
        Assert.Equal(new[] { 50, 66, 52 }, DataMatrixEncoder.AsciiCodewords("1A3"));
    }

    [Fact]
    public void Characters_above_127_use_the_upper_shift()
    {
        // "é" is 233 in ISO 8859-1: upper shift (235), then 233 - 127
        Assert.Equal(new[] { 235, 106 }, DataMatrixEncoder.AsciiCodewords("é"));
    }

    [Fact]
    public void Padding_follows_the_standard_scrambling()
    {
        var (size, words, dataCount) = DataMatrixEncoder.Codewords("A")!.Value;

        Assert.Equal(10, size);
        Assert.Equal(3, dataCount);
        Assert.Equal(new[] { 66, 129, 70 }, words.Take(3).ToArray());
    }

    [Fact]
    public void Error_correction_matches_the_published_example()
    {
        // The 10 x 10 symbol for "A": data 66 129 70, error correction 138 234 82 82 95.
        var (_, words, _) = DataMatrixEncoder.Codewords("A")!.Value;

        Assert.Equal(new[] { 138, 234, 82, 82, 95 }, words.Skip(3).ToArray());
    }

    [Theory]
    [InlineData("A")]
    [InlineData("ReportStudio 2D")]
    [InlineData("0123456789012345678901234567890123456789")]
    [InlineData("https://reas.host/payslip?id=1001&month=2026-11")]
    public void The_codewords_form_a_valid_reed_solomon_word(string text)
    {
        var (size, words, dataCount) = DataMatrixEncoder.Codewords(text)!.Value;

        Assert.True(DataMatrixEncoder.IsValidCodeword(words, words.Length - dataCount), $"size {size}");
    }

    [Fact]
    public void A_damaged_codeword_is_detected()
    {
        var (_, words, dataCount) = DataMatrixEncoder.Codewords("ReportStudio")!.Value;
        words[2] ^= 0x10;

        Assert.False(DataMatrixEncoder.IsValidCodeword(words, words.Length - dataCount));
    }

    // ---- symbol sizes ----

    [Theory]
    [InlineData("1", 10)]
    [InlineData("1234", 10)]       // two codewords
    [InlineData("ABC", 10)]        // three: the limit of the smallest symbol
    [InlineData("ABCD", 12)]
    [InlineData("ABCDE", 12)]
    [InlineData("ABCDEF", 14)]
    public void The_smallest_symbol_that_fits_is_chosen(string text, int side)
    {
        Assert.Equal(side, DataMatrixEncoder.SymbolSizeFor(text));
        Assert.Equal(side, Encode(text).Length);
        Assert.Equal(side, Encode(text)[0].Length);
    }

    [Fact]
    public void The_largest_symbol_holds_348_digits_or_174_letters()
    {
        Assert.Equal(48, DataMatrixEncoder.SymbolSizeFor(new string('7', 348)));
        Assert.Equal(48, DataMatrixEncoder.SymbolSizeFor(new string('A', 174)));
        Assert.Equal(0, DataMatrixEncoder.SymbolSizeFor(new string('A', 175)));
    }

    [Fact]
    public void Too_long_and_empty_values_are_refused_with_a_reason()
    {
        Assert.False(DataMatrixEncoder.TryEncode(new string('A', 200), out var modules, out var error));
        Assert.Empty(modules);
        Assert.Contains("too long", error);

        Assert.False(DataMatrixEncoder.TryEncode("", out _, out error));
        Assert.Contains("no value", error);
        Assert.False(DataMatrixEncoder.TryEncode(null, out _, out _));
    }

    // ---- the picture ----

    [Theory]
    [InlineData("A")]
    [InlineData("ReportStudio 2D")]
    [InlineData("a longer value that needs a bigger symbol to hold it")]
    public void The_finder_pattern_frames_the_symbol(string text)
    {
        var m = Encode(text);
        int n = m.Length;

        for (int i = 0; i < n; i++)
        {
            Assert.True(m[i][0], $"left edge, row {i}");                 // solid left edge
            Assert.True(m[n - 1][i], $"bottom edge, column {i}");        // solid bottom edge
            Assert.Equal(i % 2 == 0, m[0][i]);                           // top edge alternates, dark first
        }
        // the right edge alternates, dark on odd rows (the last row belongs to the solid bottom edge)
        for (int i = 0; i < n - 1; i++) Assert.Equal(i % 2 == 1, m[i][n - 1]);
    }

    [Fact]
    public void Large_symbols_frame_each_of_their_four_regions()
    {
        var m = Encode(new string('x', 80));        // 80 codewords: the 36 x 36 symbol, 2 x 2 regions of 18 modules
        Assert.Equal(36, m.Length);
        int region = m.Length / 2;

        for (int i = 0; i < region; i++)
        {
            foreach (int top in new[] { 0, region })
                foreach (int left in new[] { 0, region })
                {
                    Assert.True(m[top + i][left], $"left edge of the region at {top},{left}");
                    Assert.True(m[top + region - 1][left + i], $"bottom edge of the region at {top},{left}");
                    Assert.Equal(i % 2 == 0, m[top][left + i]);
                    if (i < region - 1) Assert.Equal(i % 2 == 1, m[top + i][left + region - 1]);
                }
        }
    }

    [Theory]
    [InlineData("A")]
    [InlineData("hello")]
    [InlineData("0123456789")]
    [InlineData("the quick brown fox jumps over the lazy dog 1234567890")]
    [InlineData("the quick brown fox jumps over the lazy dog, the quick brown fox jumps over the lazy dog again")]
    public void The_dark_data_modules_equal_the_one_bits_of_the_codewords(string text)
    {
        var (size, words, _) = DataMatrixEncoder.Codewords(text)!.Value;
        var m = Encode(text);
        int regions = size >= 32 ? 2 : 1;
        int region = (size - 2 * regions) / regions;      // data modules along one side of a region

        int dark = 0;
        for (int ry = 0; ry < regions; ry++)
            for (int rx = 0; rx < regions; rx++)
                for (int y = 0; y < region; y++)
                    for (int x = 0; x < region; x++)
                        if (m[ry * (region + 2) + 1 + y][rx * (region + 2) + 1 + x]) dark++;

        int ones = words.Sum(w => Convert.ToString(w, 2).Count(c => c == '1'));
        int dataArea = region * regions * region * regions;
        // Where the data area is not a whole number of bytes, four modules hold a fixed pattern: two of them dark.
        int extra = dataArea == words.Length * 8 ? 0 : 2;

        Assert.Equal(ones + extra, dark);
    }

    [Fact]
    public void The_same_value_gives_the_same_symbol_and_a_different_value_a_different_one()
    {
        static string Flat(bool[][] m) => string.Concat(m.Select(r => string.Concat(r.Select(b => b ? '1' : '0'))));

        Assert.Equal(Flat(Encode("EMP-1001")), Flat(Encode("EMP-1001")));
        Assert.NotEqual(Flat(Encode("EMP-1001")), Flat(Encode("EMP-1002")));
    }

    // ---- layout ----

    [Fact]
    public void The_layout_stays_inside_the_box_and_is_centred()
    {
        var rects = Symbol2DLayout.Rectangles(Barcode2DType.DataMatrix, "ReportStudio 2D", 120, 80);

        Assert.NotEmpty(rects);
        foreach (var (x, y, w, h) in rects)
        {
            Assert.True(x >= 0 && y >= 0, "starts inside");
            Assert.True(x + w <= 120.0001 && y + h <= 80.0001, "ends inside");
        }

        // The symbol is square, so the wide box leaves equal space left and right.
        double left = rects.Min(r => r.X), right = 120 - rects.Max(r => r.X + r.Width);
        Assert.Equal(left, right, 3);
    }

    [Fact]
    public void An_unwritable_value_or_an_empty_box_gives_no_rectangles()
    {
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.DataMatrix, "", 100, 100));
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.DataMatrix, new string('A', 300), 100, 100));
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.DataMatrix, "A", 0, 100));
    }

    [Fact]
    public void Every_type_has_a_sample_value_it_can_encode()
    {
        foreach (var type in Enum.GetValues<Barcode2DType>())
            Assert.True(Symbol2D.TryEncode(type, Symbol2D.SampleValue(type), out _, out _, out var error), $"{type}: {error}");
    }

    // ---- in a report ----

    private static ReportResult Run(string value)
    {
        var def = new ReportDefinition { Name = "Labels" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 80;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode2D, Barcode2DType = Barcode2DType.DataMatrix, Text = "{Code}", Width = 80, Height = 80 });
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Code"] = value };
        return ReportEngine.Run(def, null, new[] { row }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    [Fact]
    public void A_data_matrix_prints_with_the_value_from_the_row()
    {
        var result = Run("EMP-1001");

        var item = Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode2D);
        Assert.Equal("EMP-1001", item.Text);
        Assert.Equal(Barcode2DType.DataMatrix, item.Barcode2DType);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_value_that_does_not_fit_prints_nothing_and_warns()
    {
        var result = Run(new string('Z', 300));

        Assert.Contains(result.Warnings, w => w.Contains("2D code") && w.Contains("too long"));
        Assert.DoesNotContain(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode2D && !string.IsNullOrEmpty(i.Text));
    }

    [Fact]
    public void The_two_d_code_survives_saving()
    {
        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode2D, Barcode2DType = Barcode2DType.DataMatrix, Text = "{Code}" });

        var back = ReportDefinition.FromJson(def.ToJson()).Detail.Items[0];

        Assert.Equal(ItemKind.Barcode2D, back.Kind);
        Assert.Equal(Barcode2DType.DataMatrix, back.Barcode2DType);
    }
}
