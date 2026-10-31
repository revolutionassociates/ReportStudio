using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>CSV output (requirement 13). The page model is built by hand, so no fonts or window are needed.</summary>
public class CsvExporterTests
{
    private static RenderedItem Cell(BandKind band, int instance, double x, double y, string text) => new()
    {
        Kind = ItemKind.Text, Band = band, BandInstance = instance, X = x, Y = y, Width = 90, Height = 18, Text = text
    };

    /// <summary>Headings, two detail lines and a group total.</summary>
    private static ReportResult Sample()
    {
        var result = new ReportResult { ReportName = "Pay" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        result.Pages.Add(page);

        page.Items.Add(Cell(BandKind.PageHeader, 1, 0, 0, "Code"));
        page.Items.Add(Cell(BandKind.PageHeader, 1, 100, 0, "Amount"));
        page.Items.Add(Cell(BandKind.PageHeader, 1, 200, 0, "Date"));

        page.Items.Add(Cell(BandKind.Detail, 2, 0, 20, "007"));
        page.Items.Add(Cell(BandKind.Detail, 2, 100, 20, "12,34,567.50"));
        page.Items.Add(Cell(BandKind.Detail, 2, 200, 20, "15-03-2026"));

        page.Items.Add(Cell(BandKind.Detail, 3, 0, 40, "A,\"B\""));
        page.Items.Add(Cell(BandKind.Detail, 3, 100, 40, "7"));

        page.Items.Add(Cell(BandKind.GroupFooter, 4, 0, 60, "Total"));
        page.Items.Add(Cell(BandKind.GroupFooter, 4, 100, 60, "12,34,574.50"));
        return result;
    }

    private static string Text(byte[] csv)
    {
        Assert.True(csv.Length >= 3 && csv[0] == 0xEF && csv[1] == 0xBB && csv[2] == 0xBF, "missing UTF-8 byte-order mark");
        return Encoding.UTF8.GetString(csv, 3, csv.Length - 3);
    }

    [Fact]
    public void Writes_headings_and_detail_with_plain_numbers_and_iso_dates()
    {
        var csv = Text(CsvExporter.ToBytes(Sample()));

        Assert.Equal(
            "Code,Amount,Date\r\n" +
            "007,1234567.50,2026-03-15\r\n" +      // "007" stays text, grouping and date format removed
            "\"A,\"\"B\"\"\",7,\r\n",              // quoted, quote doubled, empty last field kept
            csv);
    }

    [Fact]
    public void Group_and_total_lines_are_included_when_asked()
    {
        var csv = Text(CsvExporter.ToBytes(Sample(), new CsvOptions { DataOnly = false }));
        Assert.EndsWith("Total,1234574.50,\r\n", csv);
    }

    [Fact]
    public void As_printed_keeps_the_text_unchanged()
    {
        var csv = Text(CsvExporter.ToBytes(Sample(), new CsvOptions { AsPrinted = true }));
        Assert.Contains("007,\"12,34,567.50\",15-03-2026\r\n", csv);
    }

    [Fact]
    public void Semicolon_separator_is_supported()
    {
        var csv = Text(CsvExporter.ToBytes(Sample(), new CsvOptions { Separator = ';' }));
        Assert.StartsWith("Code;Amount;Date\r\n007;1234567.50;2026-03-15\r\n", csv);
    }

    [Fact]
    public void Text_that_looks_like_a_formula_is_neutralised()
    {
        var result = new ReportResult();
        var page = new RenderedPage { Number = 1 };
        result.Pages.Add(page);
        page.Items.Add(Cell(BandKind.Detail, 1, 0, 0, "=HYPERLINK(\"http://x\")"));
        page.Items.Add(Cell(BandKind.Detail, 1, 100, 0, "-"));            // a lone dash is harmless
        page.Items.Add(Cell(BandKind.Detail, 1, 200, 0, "-5"));           // a negative number stays a number

        var csv = Text(CsvExporter.ToBytes(result));

        Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\")\",-,-5\r\n", csv);
    }

    [Fact]
    public void An_empty_report_gives_an_empty_file_with_only_the_byte_order_mark()
    {
        var csv = CsvExporter.ToBytes(new ReportResult());
        Assert.Equal("", Text(csv));
    }
}
