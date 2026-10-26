using System.Drawing;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>Measures wrapped text with GDI+ so CanGrow elements match what is drawn.</summary>
public sealed class GdiTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly Bitmap _bmp = new(1, 1);
    private readonly Graphics _g;

    public GdiTextMeasurer()
    {
        _bmp.SetResolution(96, 96);
        _g = Graphics.FromImage(_bmp);
        _g.PageUnit = GraphicsUnit.Pixel;
    }

    public double MeasureHeight(string text, ReportItem item, double width)
    {
        using var font = GdiPageRenderer.CreateFont(item.FontFamily, item.FontSize, item.Bold, item.Italic, item.Underline);
        using var fmt = new StringFormat(StringFormatFlags.NoClip);
        var size = _g.MeasureString(text, font, new SizeF((float)Math.Max(1, width - 4), 100000f), fmt);
        return Math.Ceiling(size.Height) + 2;
    }

    public void Dispose()
    {
        _g.Dispose();
        _bmp.Dispose();
    }
}
