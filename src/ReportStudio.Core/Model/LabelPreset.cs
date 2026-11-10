namespace ReportStudio.Core.Model;

/// <summary>
/// The layout of a common sheet of address labels (requirement 37): how many labels across and down, their size and where the first one starts.
/// Applying a preset sets the page, its margins, its columns (in "across" order) and the height of the Details band, so one detail row is one label.
/// The sizes are the makers' published ones, rounded; hold a printed test sheet against a real one before printing on label stock.
/// </summary>
public sealed record LabelPreset(string Name, bool Letter, int Columns, int Rows,
    double LabelWidthMm, double LabelHeightMm, double MarginLeftMm, double MarginTopMm, double GapMm)
{
    public const double PixelsPerMm = 96.0 / 25.4;

    public static IReadOnlyList<LabelPreset> All { get; } = new[]
    {
        new LabelPreset("Avery L7160 / J8160 (A4, 3 x 7, 63.5 x 38.1 mm)", false, 3, 7, 63.5, 38.1, 7.2, 15.1, 2.5),
        new LabelPreset("Avery L7161 (A4, 3 x 6, 63.5 x 46.6 mm)", false, 3, 6, 63.5, 46.6, 7.2, 15.5, 2.5),
        new LabelPreset("Avery L7163 / J8163 (A4, 2 x 7, 99.1 x 38.1 mm)", false, 2, 7, 99.1, 38.1, 4.65, 15.1, 2.5),
        new LabelPreset("Avery 5160 (Letter, 3 x 10, 66.7 x 25.4 mm)", true, 3, 10, 66.675, 25.4, 4.76, 12.7, 3.175),
        new LabelPreset("Avery 5163 (Letter, 2 x 5, 101.6 x 50.8 mm)", true, 2, 5, 101.6, 50.8, 3.97, 12.7, 3.18)
    };

    /// <summary>The labels on one sheet.</summary>
    public int PerSheet => Columns * Rows;

    public static double ToPixels(double mm) => mm * PixelsPerMm;

    /// <summary>Sets the page and the Details band for this sheet. Report, page header and footer bands without elements are made zero high, so the whole sheet is labels.</summary>
    public void ApplyTo(ReportDefinition def)
    {
        var page = def.Page;
        page.Width = Letter ? 816 : 794;
        page.Height = Letter ? 1056 : 1123;

        double width = ToPixels(LabelWidthMm), height = ToPixels(LabelHeightMm), gap = ToPixels(GapMm);
        double left = ToPixels(MarginLeftMm), top = ToPixels(MarginTopMm);

        page.MarginLeft = left;
        page.MarginTop = top;
        page.MarginRight = Math.Max(0, page.Width - left - Columns * width - (Columns - 1) * gap);
        // A hair less than what is left under the last line, so rounding can never push the last line to another page.
        page.MarginBottom = Math.Max(0, page.Height - top - Rows * height - 0.01);
        page.Columns = Columns;
        page.ColumnGap = gap;
        page.ColumnOrder = ColumnOrder.Across;

        def.Detail.Height = height;
        foreach (var band in new[] { def.ReportHeader, def.PageHeader, def.PageFooter, def.ReportFooter })
            if (band.Items.Count == 0) band.Height = 0;
    }

    public override string ToString() => Name;
}
