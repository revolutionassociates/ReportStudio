namespace ReportStudio.Core.Model;

/// <summary>A rectangle in the coordinates of a band (pixels), used for alignment guides.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public static Box Of(ReportItem item) => new(item.X, item.Y, item.Width, item.Height);
}

/// <summary>A line the designer draws while dragging: vertical at an X position, or horizontal at a Y position.</summary>
public readonly record struct Guide(bool Vertical, double Position);

/// <summary>
/// The arranging operations of the designer (stacking order, grouping, copying a look, alignment guides), kept apart
/// from the window code so they can be tested. The stacking order of a band is the order of its items: later items are
/// drawn on top of earlier ones.
/// </summary>
public static class ItemArrange
{
    // ---- stacking order ----

    /// <summary>Moves the items to the top of the band, keeping their order among themselves.</summary>
    public static void BringToFront(Band band, IReadOnlyCollection<ReportItem> items)
    {
        var moving = band.Items.Where(i => items.Contains(i)).ToList();
        band.Items.RemoveAll(i => items.Contains(i));
        band.Items.AddRange(moving);
    }

    /// <summary>Moves the items to the bottom of the band, keeping their order among themselves.</summary>
    public static void SendToBack(Band band, IReadOnlyCollection<ReportItem> items)
    {
        var moving = band.Items.Where(i => items.Contains(i)).ToList();
        band.Items.RemoveAll(i => items.Contains(i));
        band.Items.InsertRange(0, moving);
    }

    /// <summary>Moves each item one place up, past the item above it unless that is selected too.</summary>
    public static void BringForward(Band band, IReadOnlyCollection<ReportItem> items)
    {
        for (int i = band.Items.Count - 2; i >= 0; i--)
            if (items.Contains(band.Items[i]) && !items.Contains(band.Items[i + 1]))
                (band.Items[i], band.Items[i + 1]) = (band.Items[i + 1], band.Items[i]);
    }

    /// <summary>Moves each item one place down, past the item below it unless that is selected too.</summary>
    public static void SendBackward(Band band, IReadOnlyCollection<ReportItem> items)
    {
        for (int i = 1; i < band.Items.Count; i++)
            if (items.Contains(band.Items[i]) && !items.Contains(band.Items[i - 1]))
                (band.Items[i], band.Items[i - 1]) = (band.Items[i - 1], band.Items[i]);
    }

    // ---- grouping ----

    /// <summary>Puts the items in one group (at least two): clicking any of them selects all, and they move together.</summary>
    public static bool Group(IReadOnlyCollection<ReportItem> items)
    {
        if (items.Count < 2) return false;
        var id = Guid.NewGuid().ToString("N")[..8];
        foreach (var item in items) item.ItemGroup = id;
        return true;
    }

    /// <summary>Takes the items out of their groups.</summary>
    public static void Ungroup(IEnumerable<ReportItem> items)
    {
        foreach (var item in items) item.ItemGroup = "";
    }

    /// <summary>The selection plus every other item (in any band) that shares a group with one of them.</summary>
    public static List<ReportItem> ExpandGroups(IEnumerable<Band> bands, IEnumerable<ReportItem> selection)
    {
        var result = selection.ToList();
        var groups = result.Where(i => !string.IsNullOrEmpty(i.ItemGroup)).Select(i => i.ItemGroup).ToHashSet();
        if (groups.Count == 0) return result;

        foreach (var band in bands)
            foreach (var item in band.Items)
                if (groups.Contains(item.ItemGroup) && !result.Contains(item)) result.Add(item);
        return result;
    }

    // ---- format painter ----

    /// <summary>
    /// Copies the look of one element to another: font, alignment, colours, border and number format.
    /// Position, size, text, rules and the settings of barcodes, pictures, sub-reports and cross-tabs are not touched.
    /// </summary>
    public static void CopyAppearance(ReportItem from, ReportItem to)
    {
        to.FontFamily = from.FontFamily;
        to.FontSize = from.FontSize;
        to.Bold = from.Bold;
        to.Italic = from.Italic;
        to.Underline = from.Underline;
        to.HAlign = from.HAlign;
        to.VAlign = from.VAlign;
        to.WordWrap = from.WordWrap;
        to.ForeColor = from.ForeColor;
        to.BackColor = from.BackColor;
        to.BorderColor = from.BorderColor;
        to.BorderWidth = from.BorderWidth;
        to.Format = from.Format;
    }

    // ---- alignment guides ----

    /// <summary>
    /// How far to shift a dragged box so its left, centre or right edge lines up with one of the targets, and its top,
    /// centre or bottom with one of the vertical targets, when closer than <paramref name="tolerance"/>. Also returns
    /// the guide lines to draw (every target line that now coincides with the dragged box).
    /// </summary>
    public static (double Dx, double Dy, List<Guide> Guides) SnapToGuides(
        Box moving, IReadOnlyList<Box> xTargets, IReadOnlyList<Box> yTargets, double tolerance)
    {
        var guides = new List<Guide>();

        double dx = Nearest(
            new[] { moving.X, moving.CenterX, moving.Right },
            xTargets.SelectMany(t => new[] { t.X, t.CenterX, t.Right }), tolerance);
        double dy = Nearest(
            new[] { moving.Y, moving.CenterY, moving.Bottom },
            yTargets.SelectMany(t => new[] { t.Y, t.CenterY, t.Bottom }), tolerance);

        var shifted = moving with { X = moving.X + dx, Y = moving.Y + dy };
        AddGuides(guides, true, new[] { shifted.X, shifted.CenterX, shifted.Right },
            xTargets.SelectMany(t => new[] { t.X, t.CenterX, t.Right }));
        AddGuides(guides, false, new[] { shifted.Y, shifted.CenterY, shifted.Bottom },
            yTargets.SelectMany(t => new[] { t.Y, t.CenterY, t.Bottom }));
        return (dx, dy, guides);
    }

    private static double Nearest(double[] mine, IEnumerable<double> targets, double tolerance)
    {
        double best = 0, bestDistance = tolerance + 1;
        foreach (var target in targets)
            foreach (var m in mine)
            {
                double distance = Math.Abs(target - m);
                if (distance <= tolerance && distance < bestDistance) { best = target - m; bestDistance = distance; }
            }
        return bestDistance <= tolerance ? best : 0;
    }

    private static void AddGuides(List<Guide> guides, bool vertical, double[] mine, IEnumerable<double> targets)
    {
        foreach (var target in targets)
            if (mine.Any(m => Math.Abs(m - target) < 0.01) && !guides.Any(g => g.Vertical == vertical && Math.Abs(g.Position - target) < 0.01))
                guides.Add(new Guide(vertical, target));
    }
}
