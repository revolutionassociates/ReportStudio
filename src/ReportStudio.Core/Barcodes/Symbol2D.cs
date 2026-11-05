namespace ReportStudio.Core.Barcodes;

/// <summary>The kinds of two-dimensional code besides QR (which has its own element).</summary>
public enum Barcode2DType
{
    /// <summary>Data Matrix ECC 200: small square codes for parts, labels and pharma; up to about 173 characters or 348 digits.</summary>
    DataMatrix
}

/// <summary>One entry point for the two-dimensional codes: the dark modules of the symbol, row by row.</summary>
public static class Symbol2D
{
    /// <summary>Quiet zone kept clear on each side, in modules.</summary>
    public const int QuietModules = 2;

    /// <summary>
    /// Encodes the text. <paramref name="rowHeight"/> is how many module widths tall each row is (1 for square-module codes).
    /// Returns false with a readable reason when the text cannot be written.
    /// </summary>
    public static bool TryEncode(Barcode2DType type, string? text, out bool[][] modules, out double rowHeight, out string error)
    {
        rowHeight = 1;
        switch (type)
        {
            default:
                return DataMatrixEncoder.TryEncode(text, out modules, out error);
        }
    }

    /// <summary>A value for the designer to draw while the real value is still an {expression}.</summary>
    public static string SampleValue(Barcode2DType type) => "ReportStudio 2D";
}

/// <summary>Turns a two-dimensional code into dark rectangles that fill a box (centred, quiet zone included).</summary>
public static class Symbol2DLayout
{
    /// <summary>Dark runs as (x, y, width, height) from the box's top-left. Empty if the value cannot be encoded.</summary>
    public static IReadOnlyList<(double X, double Y, double Width, double Height)> Rectangles(
        Barcode2DType type, string? text, double width, double height)
    {
        if (width <= 0 || height <= 0 || !Symbol2D.TryEncode(type, text, out var modules, out var rowHeight, out _) || modules.Length == 0)
            return Array.Empty<(double, double, double, double)>();

        int rows = modules.Length, cols = modules[0].Length;
        double q = Symbol2D.QuietModules;
        double unit = Math.Min(width / (cols + 2 * q), height / (rows * rowHeight + 2 * q));
        double moduleHeight = unit * rowHeight;
        double left = (width - unit * cols) / 2;
        double top = (height - moduleHeight * rows) / 2;

        var rects = new List<(double, double, double, double)>();
        for (int y = 0; y < rows; y++)
        {
            int x = 0;
            while (x < cols)
            {
                if (!modules[y][x]) { x++; continue; }
                int start = x;
                while (x < cols && modules[y][x]) x++;      // one rectangle per horizontal run keeps the output small
                rects.Add((left + start * unit, top + y * moduleHeight, (x - start) * unit, moduleHeight));
            }
        }
        return rects;
    }
}
