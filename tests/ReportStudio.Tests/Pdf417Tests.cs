using System.Globalization;
using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>PDF417 (requirement 33, second code). The expected codewords were produced by the ZXing encoder.</summary>
public class Pdf417Tests
{
    private static bool[][] Encode(string text)
    {
        Assert.True(Pdf417Encoder.TryEncode(text, out var modules, out var error), error);
        return modules;
    }

    private static Pdf417Encoder.Plan Plan(string text)
    {
        var plan = Pdf417Encoder.MakePlan(text, out var error);
        Assert.True(plan != null, error);
        return plan!;
    }

    // ---- compaction ----

    [Fact]
    public void Plain_text_packs_two_characters_into_each_codeword()
    {
        Assert.Equal(new[] { 453, 178, 121, 239 }, Pdf417Encoder.HighLevel("PDF417"));
        Assert.Equal(new[] { 237, 131, 344, 807, 674, 521, 119 }, Pdf417Encoder.HighLevel("Hello World"));
        Assert.Equal(new[] { 132, 478, 481, 0, 59 }, Pdf417Encoder.HighLevel("EMP-1001"));
    }

    [Fact]
    public void A_single_letter_is_one_codeword()
    {
        // "A" is 0 in the upper-case sub-mode; the odd one out is paired with the punctuation shift (29).
        Assert.Equal(new[] { 29 }, Pdf417Encoder.HighLevel("A"));
    }

    [Fact]
    public void Tabs_and_line_breaks_stay_in_text_compaction()
    {
        Assert.Equal(new[] { 829, 1, 852, 817, 137, 149, 463, 142, 791, 253, 149 }, Pdf417Encoder.HighLevel("tab\there\nnew line"));
    }

    [Fact]
    public void Thirteen_digits_or_more_switch_to_numeric_compaction()
    {
        Assert.Equal(new[] { 902, 17, 110, 836, 811, 223 }, Pdf417Encoder.HighLevel("1234567890123"));
        Assert.Contains(902, Pdf417Encoder.HighLevel("Order 12345678901234567890"));
    }

    [Fact]
    public void A_few_digits_inside_text_are_not_worth_a_switch()
    {
        var words = Pdf417Encoder.HighLevel("Ref 1234 end");

        // text codewords never exceed 899, so any value from 900 up would be a latch or shift
        Assert.All(words, w => Assert.True(w < 900, $"unexpected mode codeword {w}"));
    }

    [Fact]
    public void Characters_that_text_compaction_cannot_hold_use_byte_compaction()
    {
        // six control characters: a multiple of six bytes latches with 924, then five codewords carry them
        var words = Pdf417Encoder.HighLevel("\u0001\u0002\u0003\u0004\u0005\u0006");

        Assert.Equal(924, words[0]);
        Assert.Equal(6, words.Count);
    }

    [Fact]
    public void Text_beyond_latin_one_is_written_as_utf8_with_an_eci_header()
    {
        var words = Pdf417Encoder.HighLevel("₹ 4500");

        Assert.Equal(927, words[0]);     // ECI
        Assert.Equal(26, words[1]);      // UTF-8
    }

    [Fact]
    public void Latin_one_text_needs_no_eci()
    {
        Assert.DoesNotContain(927, Pdf417Encoder.HighLevel("café crème"));
    }

    // ---- error correction ----

    [Fact]
    public void Error_correction_matches_the_reference_encoder()
    {
        // data = length descriptor (5) followed by the four codewords of "PDF417"; level 2 gives 8 error correction codewords
        Assert.Equal(new[] { 807, 896, 604, 841, 445, 798, 896, 674 },
            Pdf417Encoder.ErrorCorrection(new[] { 5, 453, 178, 121, 239 }, 2));
        Assert.Equal(new[] { 522, 715, 288, 230, 761, 302, 613, 486 },
            Pdf417Encoder.ErrorCorrection(new[] { 8, 237, 131, 344, 807, 674, 521, 119 }, 2));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(5, 64)]
    [InlineData(8, 512)]
    public void Each_level_has_twice_the_codewords_of_the_one_below(int level, int count)
    {
        Assert.Equal(count, Pdf417Encoder.ErrorCorrection(new[] { 3, 100, 200 }, level).Length);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(40, 2)]
    [InlineData(41, 3)]
    [InlineData(160, 3)]
    [InlineData(161, 4)]
    [InlineData(320, 4)]
    [InlineData(321, 5)]
    [InlineData(863, 5)]
    [InlineData(864, -1)]
    public void The_level_follows_the_standards_recommendation(int codewords, int level)
    {
        Assert.Equal(level, Pdf417Encoder.RecommendedLevel(codewords));
    }

    // ---- the symbol ----

    [Fact]
    public void The_symbol_fills_its_rows_and_columns_exactly()
    {
        foreach (var text in new[] { "A", "PDF417", "Revolution HRMS payslip No. 000123 for Nov-2026", new string('x', 300), new string('7', 700) })
        {
            var plan = Plan(text);

            Assert.Equal(plan.Columns * plan.Rows, plan.Codewords.Length);
            Assert.InRange(plan.Columns, Pdf417Encoder.MinColumns, Pdf417Encoder.MaxColumns);
            Assert.InRange(plan.Rows, Pdf417Encoder.MinRows, Pdf417Encoder.MaxRows);
            // the first codeword counts the data codewords (itself included), so the rest is error correction
            Assert.Equal(plan.Codewords.Length - (2 << plan.Level), plan.Codewords[0]);
            Assert.All(plan.Codewords, w => Assert.InRange(w, 0, 928));
        }
    }

    [Fact]
    public void Small_values_get_a_small_symbol()
    {
        var a = Plan("A");
        Assert.Equal((2, 5), (a.Columns, a.Rows));

        var pdf = Plan("PDF417");
        Assert.Equal((2, 7), (pdf.Columns, pdf.Rows));
        Assert.Equal(2, pdf.Level);
    }

    [Fact]
    public void Longer_values_get_more_columns_and_a_higher_level()
    {
        var plan = Plan(new string('x', 300));

        Assert.True(plan.Columns > 2);
        Assert.True(plan.Level >= 3);
    }

    [Fact]
    public void Every_row_is_framed_by_the_start_and_stop_patterns()
    {
        var m = Encode("Revolution HRMS payslip No. 000123");

        foreach (var row in m)
        {
            string bits = string.Concat(row.Select(b => b ? '1' : '0'));
            Assert.StartsWith("11111111010101000", bits);     // start pattern 8-1-1-1-1-1-1-3
            Assert.EndsWith("111111101000101001", bits);      // stop pattern 7-1-1-3-1-1-1-2-1
        }
    }

    [Fact]
    public void All_rows_have_the_same_width_of_17_modules_per_column_plus_69()
    {
        var plan = Plan("Revolution HRMS payslip No. 000123");
        var m = Encode("Revolution HRMS payslip No. 000123");

        Assert.Equal(plan.Rows, m.Length);
        Assert.All(m, row => Assert.Equal(17 * plan.Columns + 69, row.Length));
    }

    [Fact]
    public void Every_row_has_four_bars_for_each_codeword()
    {
        // each codeword is 17 modules of 4 bars and 4 spaces; the stop pattern has 5 bars
        var plan = Plan("Revolution HRMS payslip No. 000123");
        var m = Encode("Revolution HRMS payslip No. 000123");

        foreach (var row in m)
        {
            int bars = 0;
            for (int i = 0; i < row.Length; i++)
                if (row[i] && (i == 0 || !row[i - 1])) bars++;
            // start (4) + left indicator (4) + data columns (4 each) + right indicator (4) + stop (5)
            Assert.Equal(4 + 4 + 4 * plan.Columns + 4 + 5, bars);
        }
    }

    [Fact]
    public void The_same_value_gives_the_same_symbol_and_another_value_another_one()
    {
        static string Flat(bool[][] m) => string.Concat(m.Select(r => string.Concat(r.Select(b => b ? '1' : '0'))));

        Assert.Equal(Flat(Encode("EMP-1001")), Flat(Encode("EMP-1001")));
        Assert.NotEqual(Flat(Encode("EMP-1001")), Flat(Encode("EMP-1002")));
    }

    [Fact]
    public void A_large_value_still_fits_and_one_too_large_is_refused()
    {
        Assert.True(Pdf417Encoder.TryEncode(new string('7', 1000), out _, out _));
        Assert.True(Pdf417Encoder.TryEncode(new string('q', 800), out _, out _));

        Assert.False(Pdf417Encoder.TryEncode(new string('q', 2000), out var modules, out var error));
        Assert.Empty(modules);
        Assert.Contains("too long", error);
    }

    [Fact]
    public void An_empty_value_is_refused_with_a_reason()
    {
        Assert.False(Pdf417Encoder.TryEncode("", out _, out var error));
        Assert.Contains("no value", error);
        Assert.False(Pdf417Encoder.TryEncode(null, out _, out _));
    }

    // ---- through the common entry point and the layout ----

    [Fact]
    public void The_common_entry_point_reports_the_row_height_of_each_code()
    {
        Assert.True(Symbol2D.TryEncode(Barcode2DType.Pdf417, "PDF417", out _, out var pdfRows, out _));
        Assert.Equal(3, pdfRows);

        Assert.True(Symbol2D.TryEncode(Barcode2DType.DataMatrix, "PDF417", out _, out var dmRows, out _));
        Assert.Equal(1, dmRows);
    }

    [Fact]
    public void The_layout_keeps_the_stacked_rows_inside_the_box()
    {
        var rects = Symbol2DLayout.Rectangles(Barcode2DType.Pdf417, "Revolution HRMS payslip No. 000123", 240, 80);

        Assert.NotEmpty(rects);
        foreach (var (x, y, w, h) in rects)
        {
            Assert.True(x >= 0 && y >= 0, "starts inside");
            Assert.True(x + w <= 240.0001 && y + h <= 80.0001, "ends inside");
        }

        // every row is the same height, and more than one row is drawn
        Assert.Single(rects.Select(r => Math.Round(r.Height, 4)).Distinct());
        Assert.True(rects.Select(r => Math.Round(r.Y, 4)).Distinct().Count() > 3, "several rows");
    }

    [Fact]
    public void An_unwritable_value_gives_no_rectangles()
    {
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.Pdf417, "", 100, 50));
        Assert.Empty(Symbol2DLayout.Rectangles(Barcode2DType.Pdf417, new string('q', 2000), 100, 50));
    }

    // ---- in a report ----

    private static ReportResult Run(string value)
    {
        var def = new ReportDefinition { Name = "Tickets" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 60;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode2D, Barcode2DType = Barcode2DType.Pdf417, Text = "{Code}", Width = 240, Height = 60 });
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Code"] = value };
        return ReportEngine.Run(def, null, new[] { row }, new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    [Fact]
    public void A_pdf417_prints_with_the_value_from_the_row()
    {
        var result = Run("EMP-1001|Nov-2026|45300.00");

        var item = Assert.Single(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode2D);
        Assert.Equal("EMP-1001|Nov-2026|45300.00", item.Text);
        Assert.Equal(Barcode2DType.Pdf417, item.Barcode2DType);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_value_that_does_not_fit_prints_nothing_and_warns()
    {
        var result = Run(new string('q', 2000));

        Assert.Contains(result.Warnings, w => w.Contains("2D code") && w.Contains("too long"));
        Assert.DoesNotContain(result.Pages[0].Items, i => i.Kind == ItemKind.Barcode2D && !string.IsNullOrEmpty(i.Text));
    }

    [Fact]
    public void The_choice_of_code_survives_saving()
    {
        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Barcode2D, Barcode2DType = Barcode2DType.Pdf417, Text = "{Code}" });

        Assert.Equal(Barcode2DType.Pdf417, ReportDefinition.FromJson(def.ToJson()).Detail.Items[0].Barcode2DType);
    }
}
