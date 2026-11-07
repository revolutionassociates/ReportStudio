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
}
