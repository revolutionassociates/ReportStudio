using ReportStudio.Core.Model;

namespace ReportStudio.Core.Layout;

/// <summary>Rules for boxes and ellipses that every output (preview, PDF, HTML, image) shares, so a shape looks the same everywhere.</summary>
public static class ShapeGeometry
{
    /// <summary>
    /// The corner radius that can really be drawn: never negative, and never more than half of the shorter side
    /// (beyond that the corners would overlap). A bad value (negative, NaN) gives 0, a square corner.
    /// </summary>
    public static double CornerRadius(double radius, double width, double height)
    {
        if (!(radius > 0) || !(width > 0) || !(height > 0)) return 0;
        return Math.Min(radius, Math.Min(width, height) / 2);
    }

    /// <summary>
    /// True when the item's fill is a gradient: it is a box, an ellipse or a text box, and both the back colour and the
    /// gradient colour are filled in. (A colour that cannot be read is checked by each output, which then falls back to a solid fill.)
    /// </summary>
    public static bool UsesGradient(RenderedItem item) =>
        item.Kind is ItemKind.Rectangle or ItemKind.Ellipse or ItemKind.Text
        && !string.IsNullOrWhiteSpace(item.BackColor)
        && !string.IsNullOrWhiteSpace(item.GradientColor);
}
