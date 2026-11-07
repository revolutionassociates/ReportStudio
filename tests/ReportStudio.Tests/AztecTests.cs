using System.Globalization;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Aztec Code (requirement 33, third code). The expected bits and modules were produced by the ZXing encoder.</summary>
public class AztecTests
{
    private static AztecEncoder.Result Encode(string text)
    {
        var result = AztecEncoder.Encode(text, out var error);
        Assert.True(result != null, error);
        return result!;
    }

    // ---- high level: which characters cost how many bits ----

    [Theory]
    [InlineData("A", "00010")]
    [InlineData("Hello", "010011110000110011010110110000")]
    [InlineData("EMP-1001", "0011001110100010000010010111100011001000100011")]
    [InlineData("0123456789", "111100010001101000101011001111000100110101011")]
    public void Text_costs_five_bits_a_character_and_digits_four(string text, string bits)
    {
        Assert.Equal(bits, AztecEncoder.HighLevelBits(text));
    }

    [Fact]
    public void Punctuation_pairs_share_one_code()
    {
        // ". " and CR LF are single codes in the punctuation sub-mode
        Assert.Equal("1110000010000000001100011", AztecEncoder.HighLevelBits("a. b"));
        Assert.Equal("1110000010000000001000011", AztecEncoder.HighLevelBits("a\r\nb"));
        Assert.Equal("00010111101101", AztecEncoder.HighLevelBits("A."));
    }

    [Fact]
    public void A_character_no_sub_mode_can_write_uses_a_binary_shift()
    {
        // binary shift (11111), length 1 (00001), then the byte 0xE9
        Assert.Equal("111110000111101001", AztecEncoder.HighLevelBits("é"));
    }

    [Fact]
    public void Text_beyond_latin_one_starts_with_an_eci_header_for_utf8()
    {
        // FLG(n): shift to punctuation, code 0, two digits, then "2" and "6" as 4-bit digits
        Assert.StartsWith("000000000001001001000", AztecEncoder.HighLevelBits("₹ 4500"));
    }

    [Fact]
    public void Latin_one_text_has_no_eci_header()
    {
        Assert.False(AztecEncoder.HighLevelBits("café").StartsWith("0000000000", StringComparison.Ordinal));
    }

    // ---- choosing the symbol ----

    [Fact]
    public void Small_values_get_a_compact_symbol_and_large_ones_a_full_symbol()
    {
        var a = Encode("A");
        Assert.Equal((true, 1, 15, 6, 1), (a.Compact, a.Layers, a.Size, a.WordSize, a.DataWords));

        var hello = Encode("Hello");
        Assert.Equal((true, 1, 15, 6, 5), (hello.Compact, hello.Layers, hello.Size, hello.WordSize, hello.DataWords));

        var studio = Encode("ReportStudio 2D");
        Assert.Equal((true, 2, 19, 6, 16), (studio.Compact, studio.Layers, studio.Size, studio.WordSize, studio.DataWords));

        var wide = Encode(new string('x', 100));
        Assert.Equal((false, 4, 31, 8, 64), (wide.Compact, wide.Layers, wide.Size, wide.WordSize, wide.DataWords));

        var digits = Encode(new string('7', 300));
        Assert.Equal((false, 8, 49, 8, 151), (digits.Compact, digits.Layers, digits.Size, digits.WordSize, digits.DataWords));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("ReportStudio 2D")]
    [InlineData("https://reas.host/payslip?id=1001&month=2026-11")]
    [InlineData("Revolution HRMS payslip No. 000123 for Nov-2026, Employee: A. Kumar (EMP-1001), Net pay: 45,300.00")]
    public void The_size_follows_the_layer_count(string text)
    {
        var r = Encode(text);
        int expected;
        if (r.Compact) expected = 11 + 4 * r.Layers;
        else
        {
            int baseSize = 14 + 4 * r.Layers;
            expected = baseSize + 1 + 2 * ((baseSize / 2 - 1) / 15);     // reference lines every 16 modules
        }

        Assert.Equal(expected, r.Size);
        Assert.All(r.Modules, row => Assert.Equal(r.Size, row.Length));
    }

    [Fact]
    public void Word_size_grows_with_the_layers()
    {
        Assert.Equal(6, Encode(new string('x', 20)).WordSize);
        Assert.Equal(8, Encode(new string('x', 100)).WordSize);
        Assert.Equal(10, Encode(new string('x', 400)).WordSize);
        Assert.Equal(12, Encode(new string('x', 2500)).WordSize);
    }

    [Fact]
    public void The_symbol_for_a_single_letter_is_the_same_as_the_reference_encoder_makes()
    {
        string[] expected =
        {
            "000011000111101", "001110010011000", "011100000000100", "011111111111110", "100100000001010",
            "110101111101000", "110101000101010", "100101010101000", "100101000101011", "100101111101001",
            "000100000001010", "100111111111100", "100000000000001", "110110000101100", "010001010010110"
        };

        var m = Encode("A").Modules;

        Assert.Equal(expected, m.Select(row => string.Concat(row.Select(b => b ? '1' : '0'))).ToArray());
    }

    // ---- the picture ----

    [Theory]
    [InlineData("Hello")]
    [InlineData("0123456789")]
    [InlineData("ReportStudio 2D")]
    [InlineData("https://reas.host/payslip?id=1001&month=2026-11")]
    [InlineData("Revolution HRMS payslip No. 000123 for Nov-2026, Employee: A. Kumar (EMP-1001), Net pay: 45,300.00")]
    public void The_bulls_eye_has_dark_rings_at_even_distances_from_the_centre(string text)
    {
        var r = Encode(text);
        int c = r.Size / 2;
        int limit = r.Compact ? 4 : 6;

        for (int y = 0; y < r.Size; y++)
            for (int x = 0; x < r.Size; x++)
            {
                int distance = Math.Max(Math.Abs(x - c), Math.Abs(y - c));
                if (distance <= limit)
                    Assert.Equal(distance % 2 == 0, r.Modules[y][x]);
            }
    }

    [Fact]
    public void A_full_symbol_has_reference_lines_every_sixteen_modules()
    {
        var r = Encode(new string('A', 800));           // 16 layers: 83 x 83
        Assert.False(r.Compact);
        Assert.Equal(83, r.Size);
        int c = r.Size / 2;

        foreach (int line in new[] { c - 16, c + 16 })
            for (int i = 0; i < r.Size; i++)
            {
                bool dark = i % 2 == c % 2;
                Assert.Equal(dark, r.Modules[i][line]);     // a vertical line
                Assert.Equal(dark, r.Modules[line][i]);     // and a horizontal one
            }
    }

    [Fact]
    public void The_same_value_gives_the_same_symbol_and_another_value_another_one()
    {
        static string Flat(bool[][] m) => string.Concat(m.Select(r => string.Concat(r.Select(b => b ? '1' : '0'))));

        Assert.Equal(Flat(Encode("EMP-1001").Modules), Flat(Encode("EMP-1001").Modules));
        Assert.NotEqual(Flat(Encode("EMP-1001").Modules), Flat(Encode("EMP-1002").Modules));
    }

    // ---- limits ----

    [Fact]
    public void The_largest_symbol_holds_about_three_thousand_letters_or_3700_digits()
    {
        Assert.True(AztecEncoder.TryEncode(new string('z', 2999), out _, out _));
        Assert.False(AztecEncoder.TryEncode(new string('z', 3000), out _, out _));
        Assert.True(AztecEncoder.TryEncode(new string('7', 3748), out _, out _));
        Assert.False(AztecEncoder.TryEncode(new string('7', 3749), out _, out _));
    }

    [Fact]
    public void Too_long_and_empty_values_are_refused_with_a_reason()
    {
        Assert.False(AztecEncoder.TryEncode(new string('z', 5000), out var modules, out var error));
        Assert.Empty(modules);
        Assert.Contains("too long", error);

        Assert.False(AztecEncoder.TryEncode("", out _, out error));
        Assert.Contains("no value", error);
        Assert.False(AztecEncoder.TryEncode(null, out _, out _));
    }

    // ---- through the common entry point and the layout ----

    [Fact]
    public void The_common_entry_point_gives_square_modules()
    {
        Assert.True(Symbol2D.TryEncode(Barcode2DType.Aztec, "Hello", out var modules, out var rowHeight, out _));

        Assert.Equal(1, rowHeight);
        Assert.Equal(modules.Length, modules[0].Length);
    }

    [Fact]
    public void The_layout_stays_inside_the_box_and_is_centred()
    {
        var rects = Symbol2DLayout.Rectangles(Barcode2DType.Aztec, "ReportStudio 2D", 120, 80);

        Assert.NotEmpty(rects);
        foreach (var (x, y, w, h) in rects)
        {
            Assert.True(x >= 0 && y >= 0, "starts inside");
            Assert.True(x + w <= 120.0001 && y + h <= 80.0001, "ends inside");
        }

        double left = rects.Min(r => r.X), right = 120 - rects.Max(r => r.X + r.Width);
        Assert.Equal(left, right, 3);
    }

    [Fact]
    public void An_unwritable_value_gives_no_rectangles()
    {
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.Aztec, "", 100, 100));
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.Aztec, new string('z', 5000), 100, 100));
    }

    // ---- in a report ----

    private static ReportResult Run(string value)
    {
        var def = new ReportDefinition { Name = "Passes" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 80;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode2D, Barcode2DType = Barcode2DType.Aztec, Text = "{Code}", Width = 80, Height = 80 });
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Code"] = value };
        return ReportEngine.Run(def, null, new[] { row }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    [Fact]
    public void An_aztec_prints_with_the_value_from_the_row()
    {
        var result = Run("PASS-2026-000123");

        var item = Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode2D);
        Assert.Equal("PASS-2026-000123", item.Text);
        Assert.Equal(Barcode2DType.Aztec, item.Barcode2DType);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_value_that_does_not_fit_prints_nothing_and_warns()
    {
        var result = Run(new string('q', 5000));

        Assert.Contains(result.Warnings, w => w.Contains("2D code") && w.Contains("too long"));
        Assert.DoesNotContain(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode2D && !string.IsNullOrEmpty(i.Text));
    }

    [Fact]
    public void The_choice_of_code_survives_saving()
    {
        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode2D, Barcode2DType = Barcode2DType.Aztec, Text = "{Code}" });

        Assert.Equal(Barcode2DType.Aztec, ReportDefinition.FromJson(def.ToJson()).Detail.Items[0].Barcode2DType);
    }
}
