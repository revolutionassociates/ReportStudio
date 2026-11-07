namespace ReportStudio.Core.Layout;

/// <summary>Where a watermark goes and how big it may be. Every output (preview, PDF, HTML) uses these rules, so they agree.</summary>
public static class WatermarkLayout
{
    /// <summary>How much of the room along the tilted line the text may use.</summary>
    public const double Fill = 0.9;

    /// <summary>
    /// The length of the straight line through the page centre at <paramref name="angleDegrees"/>, from edge to edge of the page:
    /// the most room a line of text laid along that line can have.
    /// </summary>
    public static double AvailableLength(double pageWidth, double pageHeight, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        double c = Math.Abs(Math.Cos(radians)), s = Math.Abs(Math.Sin(radians));
        double length = double.MaxValue;
        if (c > 1e-9) length = Math.Min(length, pageWidth / c);
        if (s > 1e-9) length = Math.Min(length, pageHeight / s);
        return length;
    }

    /// <summary>
    /// The font size to use: the wanted size, or smaller when the text (<paramref name="textWidth"/> wide at the wanted size)
    /// would use more than <see cref="Fill"/> of the room. Never smaller than 4, and never larger than wanted.
    /// </summary>
    public static double FitFontSize(double fontSize, double textWidth, double pageWidth, double pageHeight, double angleDegrees)
    {
        if (!(textWidth > 0) || !(fontSize > 0)) return fontSize;
        double room = AvailableLength(pageWidth, pageHeight, angleDegrees) * Fill;
        if (textWidth <= room) return fontSize;
        return Math.Min(fontSize, Math.Max(4, fontSize * room / textWidth));
    }

    /// <summary>Opacity (0 to 1) as a colour alpha (0 to 255); a value outside the range is cut to it.</summary>
    public static int Alpha(double opacity) =>
        double.IsNaN(opacity) ? 0 : (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
}
