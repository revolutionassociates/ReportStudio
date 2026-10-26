using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>Writes an editable .docx: the report as a borderless table that keeps the column layout.</summary>
public static class WordExporter
{
    private const int TwipsPerPx = 15;   // 1 px @96dpi = 15 twips

    public static void Export(ReportResult report, string path)
    {
        var grid = Grid.Build(report);

        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = main.Document.AppendChild(new Body());

        var table = new Table();
        table.Append(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Dxa, Width = (grid.ColumnWidths.Sum() * TwipsPerPx).ToString("0") },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableCellMarginDefault(
                new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa })));

        var tg = new TableGrid();
        foreach (var w in grid.ColumnWidths)
            tg.Append(new GridColumn { Width = ((int)(w * TwipsPerPx)).ToString() });
        table.Append(tg);

        foreach (var row in grid.Rows)
        {
            var tr = new TableRow();
            int col = 0;
            foreach (var cell in row.Cells)
            {
                // Pad any columns skipped between cells so the grid stays aligned.
                while (col < cell.Column) { tr.Append(EmptyCell(grid.ColumnWidths[col])); col++; }

                double width = 0;
                for (int i = cell.Column; i < cell.Column + cell.Span && i < grid.ColumnWidths.Count; i++)
                    width += grid.ColumnWidths[i];
                tr.Append(TextCell(cell, width));
                col = cell.Column + cell.Span;
            }
            while (col < grid.ColumnWidths.Count) { tr.Append(EmptyCell(grid.ColumnWidths[col])); col++; }
            table.Append(tr);
        }

        if (grid.Rows.Count > 0) body.Append(table);
        body.Append(new Paragraph());   // Word requires a paragraph after a table and before the section end
        body.Append(SectionFor(report, grid.ColumnWidths.Sum()));
        main.Document.Save();
    }

    private static SectionProperties SectionFor(ReportResult report, double tablePx)
    {
        var page = report.Pages.FirstOrDefault();
        double wPx = page?.Width ?? 794, hPx = page?.Height ?? 1123;
        int margin = (int)Math.Max(360, (wPx - tablePx) / 2 * TwipsPerPx);

        var size = new PageSize { Width = (uint)(wPx * TwipsPerPx), Height = (uint)(hPx * TwipsPerPx) };
        if (wPx > hPx) size.Orient = PageOrientationValues.Landscape;

        return new SectionProperties(size, new PageMargin
        {
            Top = 720, Bottom = 720, Left = (uint)margin, Right = (uint)margin, Header = 360, Footer = 360, Gutter = 0
        });
    }

    private static TableCell EmptyCell(double widthPx) =>
        new(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = ((int)(widthPx * TwipsPerPx)).ToString() }),
            new Paragraph(NoSpacing()));

    private static TableCell TextCell(GridCell c, double widthPx)
    {
        var props = new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = ((int)(widthPx * TwipsPerPx)).ToString() });
        if (c.Span > 1) props.Append(new GridSpan { Val = c.Span });

        var back = ColorUtil.Parse(c.BackColor);
        if (back != null)
            props.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = Hex(back.Value) });
        props.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

        var runProps = new RunProperties();
        if (c.Bold) runProps.Append(new Bold());
        if (c.Italic) runProps.Append(new Italic());
        var fore = ColorUtil.Parse(c.ForeColor);
        if (fore != null) runProps.Append(new Color { Val = Hex(fore.Value) });
        runProps.Append(new FontSize { Val = ((int)Math.Round((c.FontSize > 0 ? c.FontSize : 10) * 2)).ToString() });

        var pPr = NoSpacing();
        pPr.Append(new Justification
        {
            Val = c.Align switch { HAlign.Center => JustificationValues.Center, HAlign.Right => JustificationValues.Right, _ => JustificationValues.Left }
        });

        var run = new Run(runProps, new Text(c.Text) { Space = SpaceProcessingModeValues.Preserve });
        return new TableCell(props, new Paragraph(pPr, run));
    }

    private static ParagraphProperties NoSpacing() =>
        new(new SpacingBetweenLines { Before = "20", After = "20", Line = "240", LineRule = LineSpacingRuleValues.Auto });

    private static string Hex(System.Drawing.Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
}
