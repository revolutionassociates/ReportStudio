namespace ReportStudio.Core.Layout;

/// <summary>
/// Shared rules for turned text (<see cref="RenderedItem.TextRotation"/>), so the picture, the PDF and the HTML agree.
/// The text turns counter-clockwise about the middle of its box; only the text turns, never the fill or border.
/// </summary>
public static class TextTurn
{
    /// <summary>The angle as 0 up to (not including) 360; NaN and infinity count as 0.</summary>
    public static double Normalize(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return 0;
        double a = degrees % 360.0;
        if (a < 0) a += 360.0;
        return a >= 360.0 ? 0 : a;
    }

    /// <summary>True when the text is turned at all (an angle within a thousandth of a degree of upright counts as upright).</summary>
    public static bool IsTurned(double degrees)
    {
        double a = Normalize(degrees);
        return a > 0.001 && a < 359.999;
    }

    /// <summary>True when the angle is nearer a quarter or three-quarter turn than a half or full one: the text then runs along the box's height.</summary>
    public static bool IsSideways(double degrees)
    {
        int q = (int)Math.Floor(Normalize(degrees) / 90.0 + 0.5) % 4;
        return q % 2 == 1;
    }

    /// <summary>The width and height the text is laid out in before it is turned: swapped when it runs sideways.</summary>
    public static (double Width, double Height) LayoutSize(double degrees, double width, double height) =>
        IsSideways(degrees) ? (height, width) : (width, height);
}
