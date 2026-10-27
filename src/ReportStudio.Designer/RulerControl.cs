namespace ReportStudio.Designer;

public enum RulerUnit { Centimetres, Inches, Pixels }

/// <summary>
/// A ruler for the design canvas. The horizontal one runs along the page width (0 = the page edge, the
/// margins are shaded); the vertical one restarts at 0 at the top of every band, so an element's Y reads
/// straight off it. A red line follows the mouse.
/// </summary>
internal sealed class RulerControl : Control
{
    public const int Thickness = 20;

    private readonly DesignCanvas _canvas;
    private readonly bool _horizontal;
    private RulerUnit _unit = RulerUnit.Centimetres;
    private Point? _pointer;

    public RulerControl(DesignCanvas canvas, bool horizontal)
    {
        _canvas = canvas;
        _horizontal = horizontal;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Dock = horizontal ? DockStyle.Top : DockStyle.Left;
        Size = new Size(Thickness, Thickness);
        BackColor = Color.FromArgb(240, 242, 246);

        _canvas.PointerMoved += p => { _pointer = p; Invalidate(); };
        _canvas.LocationChanged += (_, _) => Invalidate();       // scrolling moves the canvas inside its panel
        _canvas.SizeChanged += (_, _) => Invalidate();           // bands added / resized
        _canvas.DocumentChanged += (_, _) => Invalidate();       // margins or page size edited
        _canvas.DefinitionReplaced += (_, _) => Invalidate();
    }

    public RulerUnit Unit
    {
        get => _unit;
        set { _unit = value; Invalidate(); }
    }

    public static string UnitName(RulerUnit unit) => unit switch
    {
        RulerUnit.Centimetres => "cm",
        RulerUnit.Inches => "in",
        _ => "px"
    };

    /// <summary>Pixels per labelled unit, the number of small ticks per unit, and the value of one unit's label step.</summary>
    private (double PxPerUnit, int Divisions, int LabelStep) Scale() => _unit switch
    {
        RulerUnit.Centimetres => (96.0 / 2.54, 10, 1),   // a tick every millimetre, a number every cm
        RulerUnit.Inches => (96.0, 8, 1),                // a tick every 1/8 inch
        _ => (100.0, 10, 100)                            // a tick every 10 px, a number every 100 px
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var line = new Pen(Color.FromArgb(150, 158, 170));
        using var text = new SolidBrush(Color.FromArgb(70, 80, 95));
        using var font = new Font("Segoe UI", 6.5f);

        if (_horizontal) PaintHorizontal(g, line, text, font);
        else PaintVertical(g, line, text, font);

        using var edge = new Pen(Color.FromArgb(190, 196, 206));
        if (_horizontal) g.DrawLine(edge, 0, Height - 1, Width, Height - 1);
        else g.DrawLine(edge, Width - 1, 0, Width - 1, Height);
    }

    private void PaintHorizontal(Graphics g, Pen line, Brush text, Font font)
    {
        // the ruler sits above the vertical ruler's column, so the canvas starts Thickness pixels in
        float x0 = Thickness + _canvas.Left + _canvas.PageOrigin;
        float pageW = (float)_canvas.PageWidth;

        g.FillRectangle(Brushes.White, x0, 0, pageW, Height);
        using (var shade = new SolidBrush(Color.FromArgb(226, 229, 234)))
        {
            g.FillRectangle(shade, x0, 0, (float)_canvas.MarginLeft, Height);
            g.FillRectangle(shade, x0 + pageW - (float)_canvas.MarginRight, 0, (float)_canvas.MarginRight, Height);
        }

        DrawScale(g, line, text, font, x0, pageW, horizontal: true);

        if (_pointer is { } p)
        {
            using var mark = new Pen(Color.Red);
            float x = Thickness + _canvas.Left + p.X;
            g.DrawLine(mark, x, 0, x, Height);
        }
    }

    private void PaintVertical(Graphics g, Pen line, Brush text, Font font)
    {
        foreach (var (top, height) in _canvas.BandSpans)
        {
            float y0 = _canvas.Top + top;
            if (y0 > Height || y0 + height < 0) continue;          // scrolled out of view
            g.FillRectangle(Brushes.White, 0, y0, Width, height);
            DrawScale(g, line, text, font, y0, height, horizontal: false);
        }

        if (_pointer is { } p)
        {
            using var mark = new Pen(Color.Red);
            float y = _canvas.Top + p.Y;
            g.DrawLine(mark, 0, y, Width, y);
        }
    }

    /// <summary>Draws ticks (and numbers on the major ones) along one axis, starting at <paramref name="origin"/>.</summary>
    private void DrawScale(Graphics g, Pen line, Brush text, Font font, float origin, float length, bool horizontal)
    {
        var (pxPerUnit, divisions, labelStep) = Scale();
        double step = pxPerUnit * _canvas.Zoom / divisions;   // screen pixels between small ticks
        if (step < 2) divisions = Math.Max(1, divisions / 5);   // very small zoom: fewer ticks, same numbers
        step = pxPerUnit * _canvas.Zoom / divisions;
        int visible = horizontal ? Width : Height;

        for (int i = 0; i * step <= length; i++)
        {
            float pos = origin + (float)(i * step);
            if (pos < -20 || pos > visible + 20) continue;

            int mod = i % divisions;
            int size = mod == 0 ? Thickness - 4 : mod * 2 == divisions ? 8 : 4;

            if (horizontal) g.DrawLine(line, pos, Height - 1 - size, pos, Height - 1);
            else g.DrawLine(line, Width - 1 - size, pos, Width - 1, pos);

            if (mod != 0) continue;
            string label = (i / divisions * labelStep).ToString();
            if (horizontal) g.DrawString(label, font, text, pos + 1, 0);
            else g.DrawString(label, font, text, 0, pos + 1);
        }
    }
}
