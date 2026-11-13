using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

public class GridCell
{
    public int Column { get; init; }
    public int Span { get; init; } = 1;
    public string Text { get; init; } = "";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public double FontSize { get; init; }
    public HAlign Align { get; init; }
    public string ForeColor { get; init; } = "";
    public string BackColor { get; init; } = "";
    /// <summary>The plain field this cell shows ({Amount}), or "".</summary>
    public string Field { get; init; } = "";
    /// <summary>The total this cell shows ({Sum(Amount)}): Sum, Avg, Min, Max or Count, or "".</summary>
    public string AggFunction { get; init; } = "";
    public string AggField { get; init; } = "";
}

public class GridRow
{
    public BandKind Band { get; init; }
    /// <summary>1 = outermost group, for the rows of a group header or footer; 0 otherwise.</summary>
    public int GroupLevel { get; init; }
    public string[] GroupPath { get; init; } = Array.Empty<string>();
    public int[] GroupIds { get; init; } = Array.Empty<int>();
    /// <summary>False for the second and later visual lines of one band instance.</summary>
    public bool FirstLine { get; init; } = true;
    public List<GridCell> Cells { get; } = new();
}

/// <summary>
/// Converts the positioned page model into a table: every visual line of a band instance becomes one row,
/// and the distinct left edges of the text elements become the columns. Used by the Excel and Word
/// exporters so both produce editable, structured output rather than a picture of the page.
/// </summary>
public class Grid
{
    public List<double> ColumnWidths { get; } = new();   // report pixels
    public List<GridRow> Rows { get; } = new();

    public static Grid Build(ReportResult report)
    {
        var grid = new Grid();

        // The page header repeats on every page and the page footer is pagination furniture: keep the
        // first page header only and drop page footers, which mean nothing in a spreadsheet or flowing document.
        var texts = new List<(int Page, RenderedItem Item)>();
        foreach (var page in report.Pages)
            foreach (var it in page.Items)
            {
                if (it.Kind != ItemKind.Text || string.IsNullOrEmpty(it.Text)) continue;
                if (it.Band == BandKind.PageFooter) continue;
                if (it.Band == BandKind.PageHeader && page.Number > 1) continue;
                texts.Add((page.Number, it));
            }
        if (texts.Count == 0) return grid;

        // Column boundaries = distinct left edges (rounded to whole pixels).
        var edges = texts.Select(t => Math.Round(t.Item.X)).Distinct().OrderBy(x => x).ToList();
        double maxRight = texts.Max(t => t.Item.X + t.Item.Width);
        for (int i = 0; i < edges.Count; i++)
        {
            double next = i + 1 < edges.Count ? edges[i + 1] : maxRight;
            grid.ColumnWidths.Add(Math.Max(20, next - edges[i]));
        }

        var bands = texts
            .GroupBy(t => (t.Page, t.Item.BandInstance))
            .OrderBy(g => g.Key.Page).ThenBy(g => g.Key.BandInstance);

        foreach (var band in bands)
        {
            // Items sharing a visual line (Y within 4px) share a row; a title stacked above a subtitle
            // inside one band becomes two rows so nothing is lost.
            bool firstLine = true;
            foreach (var line in band.GroupBy(t => Math.Round(t.Item.Y / 4)).OrderBy(g => g.Key))
            {
                var head = line.First().Item;
                var row = new GridRow
                {
                    Band = head.Band, GroupLevel = head.GroupLevel, GroupPath = head.GroupPath,
                    GroupIds = head.GroupIds, FirstLine = firstLine
                };
                firstLine = false;
                int lastColumn = -1;
                foreach (var (_, it) in line.OrderBy(t => t.Item.X))
                {
                    int col = edges.IndexOf(Math.Round(it.X));
                    if (col <= lastColumn) continue;       // overlapping element on the same column: skip
                    int span = 1;
                    double right = it.X + it.Width;
                    while (col + span < edges.Count && edges[col + span] < right - 1) span++;
                    lastColumn = col + span - 1;

                    row.Cells.Add(new GridCell
                    {
                        Column = col, Span = span, Text = it.Text, Bold = it.Bold, Italic = it.Italic,
                        FontSize = it.FontSize, Align = it.HAlign, ForeColor = it.ForeColor, BackColor = it.BackColor,
                        Field = it.Field, AggFunction = it.AggFunction, AggField = it.AggField
                    });
                }
                if (row.Cells.Count > 0) grid.Rows.Add(row);
            }
        }
        return grid;
    }
}
