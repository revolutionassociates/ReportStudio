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
}

public class ReportResult
{
    public string ReportName { get; set; } = "";
    public List<RenderedPage> Pages { get; } = new();
    public int RowCount { get; set; }
    /// <summary>Non-fatal problems such as an unknown field in an expression (each message listed once).</summary>
    public List<string> Warnings { get; } = new();
}

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
