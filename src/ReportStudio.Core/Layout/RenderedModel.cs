using ReportStudio.Core.Model;

namespace ReportStudio.Core.Layout;

/// <summary>A fully resolved element on a page: all expressions evaluated, positions absolute (px @96dpi).</summary>
public class RenderedItem
{
    public ItemKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = "";
    /// <summary>
    /// Rich text: the text split into runs that differ in look (bold, italic, underline, strike, colour). Null for plain text, and when no run differs.
    /// <see cref="Text"/> always holds the same text without markup, so outputs that cannot show runs use it.
    /// </summary>
    public List<ReportStudio.Core.Text.TextRun>? Runs { get; set; }
    /// <summary>For Barcode items: the symbology; <see cref="Text"/> holds the value (empty when it cannot be encoded).</summary>
    public ReportStudio.Core.Barcodes.BarcodeType BarcodeType { get; set; }
    /// <summary>For QrCode items: the error-correction level; <see cref="Text"/> holds the value (empty when it cannot be encoded).</summary>
    public ReportStudio.Core.Barcodes.QrLevel QrLevel { get; set; } = ReportStudio.Core.Barcodes.QrLevel.Medium;
    /// <summary>For Barcode2D items: which code; <see cref="Text"/> holds the value (empty when it cannot be encoded).</summary>
    public ReportStudio.Core.Barcodes.Barcode2DType Barcode2DType { get; set; }
    public string FontFamily { get; set; } = ReportItem.DefaultFontFamily;
    public double FontSize { get; set; } = 9;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public HAlign HAlign { get; set; }
    public VAlign VAlign { get; set; }
    public bool WordWrap { get; set; } = true;
    public string ForeColor { get; set; } = "#000000";
    public string BackColor { get; set; } = "";
    public string BorderColor { get; set; } = "";
    public double BorderWidth { get; set; }
    /// <summary>Corner rounding of a box, in pixels (0 = square). See <see cref="ShapeGeometry.CornerRadius"/> for the usable value.</summary>
    public double CornerRadius { get; set; }
    /// <summary>Degrees the text is turned counter-clockwise about the middle of the box (0 = upright). See <see cref="TextRotation"/>.</summary>
    public double TextRotation { get; set; }
    /// <summary>Second colour of a gradient fill, or "" for a solid fill. See <see cref="ShapeGeometry.UsesGradient"/>.</summary>
    public string GradientColor { get; set; } = "";
    public GradientDirection GradientDirection { get; set; }
    /// <summary>Cleaned hyperlink address (http, https, mailto, tel, or #Bookmark), or "" for none. See <see cref="LinkTarget"/>.</summary>
    public string Link { get; set; } = "";
    /// <summary>Name of the bookmark placed at this item, or "".</summary>
    public string Bookmark { get; set; } = "";
    /// <summary>Title of this place in the PDF bookmark panel and the table of contents, or "" when not listed.</summary>
    public string OutlineTitle { get; set; } = "";
    /// <summary>Drill-down: set on the items of a collapsible group's header; clicking one opens or closes that group instance.</summary>
    public string DrillKey { get; set; } = "";
    /// <summary>Drill-through: the report to open when this item is clicked (name or .rsr file next to this report), or "".</summary>
    public string DrillReport { get; set; } = "";
    /// <summary>Drill-through: the parameter values to open it with.</summary>
    public Dictionary<string, string> DrillParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int OutlineLevel { get; set; } = 1;
    public byte[]? Image { get; set; }
    /// <summary>Which band produced the item.</summary>
    public BandKind Band { get; set; }
    /// <summary>Sequence number of the band instance on the page, so items of one band line up in one row.</summary>
    public int BandInstance { get; set; }
}

public class RenderedPage
{
    /// <summary>Absolute page index in the output (1-based).</summary>
    public int Number { get; set; }
    /// <summary>Numbering section (1-based). Changes where a group restarts page numbers, e.g. one per employee.</summary>
    public int Section { get; set; } = 1;
    public double Width { get; set; }
    public double Height { get; set; }
    public List<RenderedItem> Items { get; } = new();
    /// <summary>Faint text drawn behind this page's items, already resolved (expressions filled in). Null = none.</summary>
    public RenderedWatermark? Watermark { get; set; }
    /// <summary>The colour behind the whole page, drawn before everything else. Null = the plain page.</summary>
    public RenderedBackground? Background { get; set; }
}

/// <summary>What is painted behind a whole page: a colour, or a gradient from it to <see cref="GradientColor"/>.</summary>
public class RenderedBackground
{
    public string Color { get; set; } = "";
    /// <summary>Empty = a single colour.</summary>
    public string GradientColor { get; set; } = "";
    public GradientDirection GradientDirection { get; set; }
}

/// <summary>The watermark of one page, ready to draw: see <see cref="WatermarkLayout"/> for where it goes.</summary>
public class RenderedWatermark
{
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#9CA3AF";
    public double Opacity { get; set; } = 0.3;
    /// <summary>Degrees, counter-clockwise.</summary>
    public double Angle { get; set; } = 45;
    /// <summary>Points.</summary>
    public double FontSize { get; set; } = 72;
    public bool Bold { get; set; } = true;
    /// <summary>The picture (a checked picture or safe SVG), or null for a text-only watermark. See <see cref="WatermarkLayout.PictureSize"/>.</summary>
    public byte[]? Image { get; set; }
    /// <summary>Percent of the page width the picture may use.</summary>
    public double ImageSize { get; set; } = 50;
}

public class ReportResult
{
    public string ReportName { get; set; } = "";
    public List<RenderedPage> Pages { get; } = new();
    public int RowCount { get; set; }
    /// <summary>Non-fatal problems such as an unknown field in an expression (each message listed once).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Every bookmark in the output, in page order (a name used twice keeps its first place).</summary>
    public List<BookmarkInfo> FindBookmarks()
    {
        var list = new List<BookmarkInfo>();
        for (int p = 0; p < Pages.Count; p++)
            foreach (var item in Pages[p].Items)
                if (item.Bookmark.Length > 0 && !list.Any(b => string.Equals(b.Name, item.Bookmark, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new BookmarkInfo(item.Bookmark, p, item.Y));
        return list;
    }

    /// <summary>Every place with an outline title, in reading order (page, then height).</summary>
    private List<(int Page, RenderedItem Item)> OutlineItems()
    {
        var list = new List<(int, RenderedItem)>();
        for (int p = 0; p < Pages.Count; p++)
            list.AddRange(Pages[p].Items.Where(i => i.OutlineTitle.Length > 0).OrderBy(i => i.Y).Select(i => (p, i)));
        return list;
    }

    /// <summary>Gives each outline place without a bookmark of its own one (_o0, _o1, ...), so a table of contents can link to it.</summary>
    public void AssignOutlineBookmarks()
    {
        var items = OutlineItems();
        for (int i = 0; i < items.Count; i++)
            if (items[i].Item.Bookmark.Length == 0) items[i].Item.Bookmark = "_o" + i;
    }

    /// <summary>The table of contents / PDF bookmarks: every outline place in reading order, with the page number as {Page} prints it.</summary>
    public List<OutlineEntry> FindOutline()
    {
        var result = new List<OutlineEntry>();
        foreach (var (page, item) in OutlineItems())
        {
            int label = Pages.Take(page + 1).Count(p => p.Section == Pages[page].Section);
            result.Add(new OutlineEntry(item.OutlineTitle, Math.Max(1, item.OutlineLevel), page, item.Y, label, item.Bookmark));
        }
        return result;
    }

    /// <summary>The bookmark with this name (without the leading #), or null.</summary>
    public BookmarkInfo? FindBookmark(string name) =>
        FindBookmarks().FirstOrDefault(b => string.Equals(b.Name, name.TrimStart('#'), StringComparison.OrdinalIgnoreCase));
}

/// <summary>One line of the table of contents: title, level (1 = main), page index (from 0), height on it, the page number shown, and its bookmark name.</summary>
public sealed record OutlineEntry(string Title, int Level, int PageIndex, double Y, int PageLabel, string Bookmark);

/// <summary>A named place in the output: the page (index from 0) and the height on it, in pixels.</summary>
public sealed record BookmarkInfo(string Name, int PageIndex, double Y);

/// <summary>Measures wrapped text so CanGrow elements can size themselves. The GDI+ implementation lives in Export.</summary>
public interface ITextMeasurer
{
    double MeasureHeight(string text, ReportItem item, double width);
}

/// <summary>Rough, dependency-free estimate used when no real measurer is supplied.</summary>
public sealed class EstimatingTextMeasurer : ITextMeasurer
{
    public double MeasureHeight(string text, ReportItem item, double width)
    {
        double px = item.FontSize * 96.0 / 72.0;
        double charWidth = px * 0.55;
        int perLine = Math.Max(1, (int)(Math.Max(1, width - 4) / charWidth));
        int lines = 0;
        foreach (var para in (text ?? "").Split('\n'))
            lines += Math.Max(1, (int)Math.Ceiling(para.Length / (double)perLine));
        return lines * px * 1.25 + 2;
    }
}
