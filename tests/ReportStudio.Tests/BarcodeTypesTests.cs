using System.Globalization;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>EAN-13, EAN-8, UPC-A, Interleaved 2 of 5 and Codabar (requirement 32).</summary>
public class BarcodeTypesTests
{
    private static int[] Runs(BarcodeType type, string text)
    {
        Assert.True(BarcodeEncoder.TryEncode(type, text, out var runs, out var error), error);
        return runs;
    }

    /// <summary>The bars as module bits: runs alternate bar (1) and space (0), starting with a bar.</summary>
    private static string Bits(int[] runs) =>
        string.Concat(runs.Select((r, i) => new string(i % 2 == 0 ? '1' : '0', r)));

    // ---- check digits ----

    [Theory]
    [InlineData("400638133393", 1)]
    [InlineData("590123412345", 7)]
    [InlineData("03600029145", 2)]    // UPC-A
    [InlineData("7351353", 7)]        // EAN-8
    public void The_check_digit_follows_the_retail_rule(string payload, int expected)
    {
        Assert.Equal(expected, BarcodeEncoder.CheckDigit(payload));
    }

    [Fact]
    public void A_missing_check_digit_is_added_and_a_given_one_is_checked()
    {
        Assert.Equal(Bits(Runs(BarcodeType.Ean13, "4006381333931")), Bits(Runs(BarcodeType.Ean13, "400638133393")));
        Assert.False(BarcodeEncoder.TryEncode(BarcodeType.Ean13, "4006381333930", out _, out var error));
        Assert.Contains("should be 1", error);
    }

    [Theory]
    [InlineData(BarcodeType.Ean13, "12345")]
    [InlineData(BarcodeType.Ean13, "40063813339312")]
    [InlineData(BarcodeType.Ean13, "40063813339A")]
    [InlineData(BarcodeType.Ean8, "123456")]
    [InlineData(BarcodeType.UpcA, "0360002914")]
    [InlineData(BarcodeType.Itf, "12A4")]
    [InlineData(BarcodeType.Itf, "")]
    [InlineData(BarcodeType.Codabar, "12x45")]
    [InlineData(BarcodeType.Codabar, "A123")]
    [InlineData(BarcodeType.Codabar, "A1A2B")]
    [InlineData(BarcodeType.Codabar, "AB")]
    public void Values_that_do_not_fit_are_rejected_with_a_reason(BarcodeType type, string text)
    {
        Assert.False(BarcodeEncoder.TryEncode(type, text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_printed_text_has_the_check_digit_and_leading_zero()
    {
        Assert.Equal("4006381333931", BarcodeEncoder.PrintedText(BarcodeType.Ean13, "400638133393"));
        Assert.Equal("4006381333931", BarcodeEncoder.PrintedText(BarcodeType.Ean13, "4006381333931"));
        Assert.Equal("036000291452", BarcodeEncoder.PrintedText(BarcodeType.UpcA, "03600029145"));
        Assert.Equal("73513537", BarcodeEncoder.PrintedText(BarcodeType.Ean8, "7351353"));
        Assert.Equal("0123", BarcodeEncoder.PrintedText(BarcodeType.Itf, "123"));
        Assert.Equal("A12B", BarcodeEncoder.PrintedText(BarcodeType.Codabar, "a12b"));
        Assert.Equal("bad", BarcodeEncoder.PrintedText(BarcodeType.Ean13, "bad"));   // unencodable: unchanged
    }

    // ---- bar patterns ----

    [Fact]
    public void An_ean13_has_95_modules_with_guards_at_start_middle_and_end()
    {
        var bits = Bits(Runs(BarcodeType.Ean13, "4006381333931"));

        Assert.Equal(95, bits.Length);
        Assert.Equal("101", bits[..3]);
        Assert.Equal("01010", bits.Substring(45, 5));
        Assert.Equal("101", bits[^3..]);
    }

    [Fact]
    public void The_first_digit_of_an_ean13_picks_which_left_digits_use_the_second_set()
    {
        // first digit 4 = L G L L G G: left digits are 0 0 6 3 8 1
        var bits = Bits(Runs(BarcodeType.Ean13, "4006381333931"));

        Assert.Equal("0001101", bits.Substring(3, 7));    // 0 in set L
        Assert.Equal("0100111", bits.Substring(10, 7));   // 0 in set G
        Assert.Equal("0101111", bits.Substring(17, 7));   // 6 in set L
    }

    [Fact]
    public void The_right_half_of_an_ean13_uses_the_third_set()
    {
        // right digits are 3 3 3 9 3 1: 3 = 1000010 in set R
        var bits = Bits(Runs(BarcodeType.Ean13, "4006381333931"));

        Assert.Equal("1000010", bits.Substring(50, 7));
        Assert.Equal("1110100", bits.Substring(50 + 3 * 7, 7));    // 9 in set R
    }

    [Fact]
    public void A_upc_a_is_an_ean13_with_a_leading_zero()
    {
        Assert.Equal(Bits(Runs(BarcodeType.Ean13, "0036000291452")), Bits(Runs(BarcodeType.UpcA, "036000291452")));
        Assert.Equal(Bits(Runs(BarcodeType.UpcA, "036000291452")), Bits(Runs(BarcodeType.UpcA, "03600029145")));
        Assert.Equal(95, Bits(Runs(BarcodeType.UpcA, "03600029145")).Length);
    }

    [Fact]
    public void An_ean8_has_67_modules_and_all_left_digits_use_the_first_set()
    {
        var bits = Bits(Runs(BarcodeType.Ean8, "73513537"));

        Assert.Equal(67, bits.Length);
        Assert.Equal("101", bits[..3]);
        Assert.Equal("01010", bits.Substring(31, 5));
        Assert.Equal("101", bits[^3..]);
        Assert.Equal("0111011", bits.Substring(3, 7));   // 7 in set L
        Assert.Equal("1000010", bits.Substring(36, 7));   // 3 in set R
    }

    [Fact]
    public void Itf_pairs_digits_with_start_and_stop_patterns()
    {
        var runs = Runs(BarcodeType.Itf, "1234");

        Assert.Equal(4 + 20 + 3, runs.Length);
        Assert.Equal(new[] { 1, 1, 1, 1 }, runs.Take(4));
        Assert.Equal(new[] { 3, 1, 1 }, runs.TakeLast(3));
        Assert.Equal(4 + 2 * 18 + 5, runs.Sum());           // each pair: 10 elements, 4 of them wide
    }

    [Fact]
    public void An_itf_with_an_odd_number_of_digits_gets_a_leading_zero()
    {
        Assert.Equal(Runs(BarcodeType.Itf, "0123"), Runs(BarcodeType.Itf, "123"));
    }

    [Fact]
    public void Codabar_with_and_without_start_and_stop_letters_gives_the_same_bars()
    {
        var framed = Runs(BarcodeType.Codabar, "A12345A");

        Assert.Equal(framed, Runs(BarcodeType.Codabar, "12345"));
        Assert.Equal(framed, Runs(BarcodeType.Codabar, "a12345a"));
        Assert.Equal(7 * 7 + 6, framed.Length);
        Assert.Equal(13 + 5 * 11 + 13 + 6, framed.Sum());   // wide = 3 modules, gaps between characters
    }

    [Fact]
    public void Codabar_accepts_its_symbols_and_different_start_and_stop_letters()
    {
        Assert.NotEmpty(Runs(BarcodeType.Codabar, "B123-45$:/.+C"));
    }

    // ---- layout, designer sample, engine ----

    [Fact]
    public void Bars_leave_the_wider_quiet_zone_of_retail_codes()
    {
        var bars = BarcodeLayout.Bars(BarcodeType.Ean13, "4006381333931", 117);   // 117 = 95 + 2 * 11 modules, so one module = 1

        Assert.Equal(11, bars[0].X, 6);
        Assert.Equal(1, bars[0].Width, 6);
        Assert.Equal(13, bars[1].X, 6);                      // start guard 1-0-1
        Assert.Equal(106, bars[^1].X + bars[^1].Width, 6);   // 11 + 95 modules
        Assert.All(bars, b => Assert.True(b.Width > 0));
    }

    [Fact]
    public void Every_type_has_a_sample_value_that_encodes()
    {
        foreach (var type in Enum.GetValues<BarcodeType>())
            Assert.True(BarcodeEncoder.TryEncode(type, BarcodeEncoder.SampleValue(type), out _, out var error), $"{type}: {error}");
    }

    private static ReportResult Run(BarcodeType type, string value)
    {
        var def = new ReportDefinition { Name = "Codes" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 60;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode, BarcodeType = type, Text = "{Code}", Width = 200, Height = 60 });
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Code"] = value };
        return ReportEngine.Run(def, null, new[] { row }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    [Fact]
    public void The_caption_under_an_ean_shows_the_check_digit_that_was_added()
    {
        var items = Run(BarcodeType.Ean13, "400638133393").Pages[0].Items;

        Assert.Contains(items, i => i.Kind == ItemKind.Text && i.Text == "4006381333931");
        Assert.Contains(items, i => i.Kind == ItemKind.Barcode && i.BarcodeType == BarcodeType.Ean13);
    }

    [Fact]
    public void A_wrong_ean_prints_nothing_and_warns()
    {
        var result = Run(BarcodeType.Ean13, "4006381333930");

        Assert.Contains(result.Warnings, w => w.Contains("check digit"));
        Assert.DoesNotContain(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode && i.Text.Length > 0);
    }

    [Fact]
    public void The_new_types_survive_saving()
    {
        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode, BarcodeType = BarcodeType.Codabar });

        Assert.Equal(BarcodeType.Codabar, ReportDefinition.FromJson(def.ToJson()).Detail.Items[0].BarcodeType);
    }
}
