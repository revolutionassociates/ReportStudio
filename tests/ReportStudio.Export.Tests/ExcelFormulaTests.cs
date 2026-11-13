using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Totals as live Excel formulas, and one sheet per group (requirement 42). The report runs through the real engine; no fonts or window are needed.</summary>
public class ExcelFormulaTests
{
    private static Dictionary<string, object?> Row(string dept, string name, decimal amount) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Dept"] = dept, ["Name"] = name, ["Amount"] = amount };

    private static Dictionary<string, object?>[] Rows() => new[]
    {
        Row("A", "a1", 10), Row("A", "a2", 20),
        Row("B", "b1", 50),
        Row("C", "c1", 5), Row("C", "c2", 5)
    };

    private static ReportItem Text(double x, string text, string format = "") =>
        new() { Kind = ItemKind.Text, Text = text, Format = format, X = x, Width = 100, Height = 18 };

    private static ReportDefinition Report(string detailAmountVisibleWhen = "")
    {
        var def = new ReportDefinition { Name = "Pay" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.PageFooter.Height = 0;
        def.Detail.Height = 20;
        def.ReportFooter.Height = 20;
        def.Groups.Add(new GroupDefinition { Expression = "Dept" });
        def.Groups[0].Header.Height = 20;
        def.Groups[0].Footer.Height = 20;
        def.Groups[0].Header.Items.Add(Text(0, "{Dept}"));
        def.Detail.Items.Add(Text(0, "{Name}"));
        var amount = Text(100, "{Amount}", "N2");
        amount.VisibleWhen = detailAmountVisibleWhen;
        def.Detail.Items.Add(amount);
        def.Groups[0].Footer.Items.Add(Text(0, "Total"));
        def.Groups[0].Footer.Items.Add(Text(100, "{Sum(Amount)}", "N2"));
        def.ReportFooter.Items.Add(Text(0, "Grand"));
        def.ReportFooter.Items.Add(Text(100, "{Sum(Amount)}", "N2"));
        def.Normalize();
        return def;
    }

    private static byte[] Workbook(ReportDefinition def, Dictionary<string, object?>[] rows, ExcelOptions? options = null)
    {
        var result = ReportEngine.Run(def, null, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });
        using var ms = new MemoryStream();
        ExcelExporter.Export(result, ms, CultureInfo.InvariantCulture, options);
        return ms.ToArray();
    }

    private static Cell CellAt(byte[] xlsx, string sheet, string reference)
    {
        using var ms = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var book = doc.WorkbookPart!;
        var entry = book.Workbook.Descendants<Sheet>().First(s => s.Name == sheet);
        var part = (WorksheetPart)book.GetPartById(entry.Id!.Value!);
        // A copy, so it can be read after the package is closed.
        return (Cell)part.Worksheet.Descendants<Cell>().First(c => c.CellReference == reference).CloneNode(true);
    }

    private static List<string> SheetNames(byte[] xlsx)
    {
        using var ms = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(ms, false);
        return doc.WorkbookPart!.Workbook.Descendants<Sheet>().Select(s => s.Name!.Value!).ToList();
    }

    [Fact]
    public void Totals_become_formulas_over_the_detail_cells_of_their_group()
    {
        var xlsx = Workbook(Report(), Rows());
        var sheet = SheetNames(xlsx).Single();

        // rows: 1 header A, 2-3 details, 4 footer A, 5 header B, 6 detail, 7 footer B, 8 header C, 9-10 details, 11 footer C, 12 grand total
        Assert.Equal("SUM(B2:B3)", CellAt(xlsx, sheet, "B4").CellFormula?.Text);
        Assert.Equal("SUM(B6)", CellAt(xlsx, sheet, "B7").CellFormula?.Text);
        Assert.Equal("SUM(B9:B10)", CellAt(xlsx, sheet, "B11").CellFormula?.Text);
        Assert.Equal("SUM(B2:B3,B6,B9:B10)", CellAt(xlsx, sheet, "B12").CellFormula?.Text);
    }

    [Fact]
    public void A_formula_cell_still_holds_the_printed_number_for_viewers_that_do_not_recalculate()
    {
        var xlsx = Workbook(Report(), Rows());
        var cell = CellAt(xlsx, SheetNames(xlsx).Single(), "B4");

        Assert.Equal(30m, decimal.Parse(cell.CellValue!.Text, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Detail_cells_and_labels_stay_plain_values()
    {
        var xlsx = Workbook(Report(), Rows());
        var sheet = SheetNames(xlsx).Single();

        Assert.Null(CellAt(xlsx, sheet, "B2").CellFormula);
        Assert.Null(CellAt(xlsx, sheet, "A4").CellFormula);
    }

    [Fact]
    public void Formulas_can_be_switched_off()
    {
        var xlsx = Workbook(Report(), Rows(), new ExcelOptions { Formulas = false });
        var sheet = SheetNames(xlsx).Single();

        Assert.Null(CellAt(xlsx, sheet, "B4").CellFormula);
        Assert.Null(CellAt(xlsx, sheet, "B12").CellFormula);
    }

    [Fact]
    public void A_total_that_excel_could_not_reproduce_stays_a_plain_number()
    {
        // The amount of B (50) is not printed, so no cells add up to the printed totals of B and of the report.
        var xlsx = Workbook(Report("Amount < 40"), Rows());
        var sheet = SheetNames(xlsx).Single();

        Assert.Null(CellAt(xlsx, sheet, "B7").CellFormula);
        Assert.Null(CellAt(xlsx, sheet, "B12").CellFormula);
        Assert.Equal("SUM(B2:B3)", CellAt(xlsx, sheet, "B4").CellFormula?.Text);   // the group that prints completely keeps its formula
    }

    [Fact]
    public void One_sheet_per_group_is_named_after_the_group_and_keeps_its_own_rows()
    {
        var xlsx = Workbook(Report(), Rows(), new ExcelOptions { SheetPerGroupLevel = 1 });

        Assert.Equal(new[] { "A", "B", "C" }, SheetNames(xlsx));
        // sheet A: 1 header, 2-3 details, 4 footer. sheet B: 1 header, 2 detail, 3 footer.
        Assert.Equal("SUM(B2:B3)", CellAt(xlsx, "A", "B4").CellFormula?.Text);
        Assert.Equal("SUM(B2)", CellAt(xlsx, "B", "B3").CellFormula?.Text);
        Assert.Equal("SUM(B2:B3)", CellAt(xlsx, "C", "B4").CellFormula?.Text);
    }

    [Fact]
    public void The_report_total_adds_up_the_cells_of_every_sheet()
    {
        var xlsx = Workbook(Report(), Rows(), new ExcelOptions { SheetPerGroupLevel = 1 });

        // The report footer follows the last group, so it sits on sheet C, row 5, and refers to the other sheets by name.
        Assert.Equal("SUM('A'!B2:B3,'B'!B2,B2:B3)", CellAt(xlsx, "C", "B5").CellFormula?.Text);
    }

    [Fact]
    public void Sheet_names_lose_forbidden_characters_and_are_made_unique()
    {
        var rows = new[] { Row("A/B", "x", 1), Row("A:B", "y", 2) };
        var xlsx = Workbook(Report(), rows, new ExcelOptions { SheetPerGroupLevel = 1 });

        Assert.Equal(new[] { "AB", "AB (2)" }, SheetNames(xlsx));
    }

    [Fact]
    public void A_split_deeper_than_the_report_has_groups_gives_one_sheet()
    {
        var xlsx = Workbook(Report(), Rows(), new ExcelOptions { SheetPerGroupLevel = 3 });

        Assert.Single(SheetNames(xlsx));
    }

    [Fact]
    public void Totals_are_recognised_from_the_designed_text()
    {
        var item = new RenderedItem();
        ExportHints.Apply("{Sum(Amount)}", item);
        Assert.Equal("Sum", item.AggFunction);
        Assert.Equal("Amount", item.AggField);

        var count = new RenderedItem();
        ExportHints.Apply("{ count() }", count);
        Assert.Equal("Count", count.AggFunction);
        Assert.Equal("", count.AggField);

        var field = new RenderedItem();
        ExportHints.Apply("{Amount}", field);
        Assert.Equal("Amount", field.Field);

        var mixed = new RenderedItem();
        ExportHints.Apply("Total {Sum(Amount)}", mixed);
        Assert.Equal("", mixed.AggFunction);
        Assert.Equal("", mixed.Field);

        var noField = new RenderedItem();
        ExportHints.Apply("{Sum()}", noField);
        Assert.Equal("", noField.AggFunction);
    }
}
