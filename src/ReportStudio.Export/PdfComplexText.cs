using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Text;
using ReportStudio.Export.Pdf;

namespace ReportStudio.Export;

/// <summary>
/// Draws Hindi, Tamil and the other Indic scripts into the PDF. PDFsharp places glyphs one by one and does no
/// shaping, so conjuncts and reordered vowel signs would be wrong. GDI+ does shape these scripts, so the text is laid
/// out by GDI+ (same wrapping, alignment and ellipsis as the viewer) and written to the PDF as filled outlines.
/// The result looks exactly like the viewer on any PDF reader. The price: such text is a drawing, so it cannot be
/// selected or searched in the PDF. Latin text is untouched and stays real text.
/// The font is the script's Noto font from the Fonts folder if present, otherwise an installed Windows font
/// (Nirmala UI and friends). With no font for the script nothing changes: the normal text path is used.
/// </summary>
public static class PdfComplexText
{
    private static readonly ConcurrentDictionary<IndicScript, FontFamily> Found = new();

    /// <summary>The font that will be used for a script, or null when none is available.</summary>
    public static FontFamily? FindFamily(IndicScript script)
    {
        if (script == IndicScript.None) return null;
        if (Found.TryGetValue(script, out var cached)) return cached;

        foreach (var name in ComplexScript.PreferredFamilies(script))
        {
            var family = FontCatalog.FindFamily(name)
                ?? FontFamily.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (family == null) continue;
            Found[script] = family;           // a missing font is not remembered, so a later AddDirectory still works
            return family;
        }
        return null;
    }

    /// <summary>True when the text was drawn here; false means the caller draws it the normal way.</summary>
    internal static bool TryDraw(PdfPage page, RenderedItem it, RectangleF rect)
    {
        var script = ComplexScript.Detect(it.Text);
        var family = FindFamily(script);
        if (family == null || string.IsNullOrEmpty(it.Text)) return false;

        var style = FontStyle.Regular;
        if (it.Bold) style |= FontStyle.Bold;
        if (it.Italic) style |= FontStyle.Italic;
        var drawn = style;
        if (!family.IsStyleAvailable(drawn)) drawn = FontStyle.Regular;      // fonts without a bold face print regular
        if (it.Underline) drawn |= FontStyle.Underline;

        float em = (float)(it.FontSize * 96.0 / 72.0);
        var inner = new RectangleF(rect.X + 2, rect.Y, Math.Max(1, rect.Width - 4), rect.Height);

        using var format = GdiPageRenderer.CreateFormat(it);
        using var path = new GraphicsPath(FillMode.Winding);
        path.AddString(it.Text, family, (int)drawn, em, inner, format);
        if (path.PointCount == 0) return false;

        using var matrix = new Matrix();
        path.Flatten(matrix, 0.05f);                                          // curves become short lines

        var outline = new List<IReadOnlyList<PointF>>();
        var points = path.PathPoints;
        var types = path.PathTypes;
        var current = new List<PointF>();
        for (int i = 0; i < points.Length; i++)
        {
            if ((types[i] & 0x07) == 0 && current.Count > 0)                  // a new contour starts
            {
                outline.Add(current.ToArray());
                current.Clear();
            }
            current.Add(points[i]);
            if ((types[i] & 0x80) != 0)                                       // this point closes the contour
            {
                outline.Add(current.ToArray());
                current.Clear();
            }
        }
        if (current.Count > 0) outline.Add(current.ToArray());

        // Winding rule, like the GDI+ path it came from: letters with holes keep them.
        page.FillPolygons(outline, ColorUtil.Parse(it.ForeColor) ?? Color.Black, evenOdd: false);
        return true;
    }
}
