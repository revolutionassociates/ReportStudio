using ReportStudio.Core.Layout;
using ReportStudio.Export;
using System.ComponentModel;
using System.Drawing;

namespace ReportStudio.UI;

/// <summary>Paints one rendered report page, scaled by <see cref="Zoom"/>, with a thin paper border.</summary>
public sealed class PageSurface : Control
{
    private RenderedPage? _page;
    private float _zoom = 1f;

    public PageSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RenderedPage? Page
    {
        get => _page;
        set { _page = value; UpdateSize(); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, 0.1f, 5f); UpdateSize(); Invalidate(); }
    }

    private void UpdateSize()
    {
        Size = _page == null
            ? new Size(1, 1)
            : new Size((int)Math.Ceiling(_page.Width * _zoom), (int)Math.Ceiling(_page.Height * _zoom));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_page == null) { e.Graphics.Clear(Color.White); return; }
        GdiPageRenderer.Draw(e.Graphics, _page, _zoom);
        using var pen = new Pen(Color.FromArgb(120, 120, 120));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
