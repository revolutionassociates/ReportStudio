using System.Drawing;
using ReportStudio.Core.Layout;
using ReportStudio.Export;

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

    [System.ComponentModel.Browsable(false),
     System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public RenderedPage? Page
    {
        get => _page;
        set { _page = value; UpdateSize(); Invalidate(); }
    }

    [System.ComponentModel.Browsable(false),
     System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
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

    /// <summary>Raised when a clickable element (a link, a drill-down header or a drill-through item) is clicked.</summary>
    public event EventHandler<RenderedItem>? LinkClicked;

    private RenderedItem? LinkAt(Point location)
    {
        if (_page == null) return null;
        double x = location.X / _zoom, y = location.Y / _zoom;
        for (int i = _page.Items.Count - 1; i >= 0; i--)   // the item drawn last is on top
        {
            var it = _page.Items[i];
            if ((it.Link.Length > 0 || it.DrillKey.Length > 0 || it.DrillReport.Length > 0) && x >= it.X && x <= it.X + it.Width && y >= it.Y && y <= it.Y + it.Height) return it;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = LinkAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = LinkAt(e.Location);
        if (hit != null) LinkClicked?.Invoke(this, hit);
    }

    private IReadOnlyCollection<RenderedItem> _highlights = Array.Empty<RenderedItem>();
    private RenderedItem? _current;

    /// <summary>Search results on this page: all drawn with a yellow marker, the one being looked at in orange.</summary>
    public void SetHighlights(IReadOnlyCollection<RenderedItem> all, RenderedItem? current)
    {
        _highlights = all;
        _current = current;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_page == null) { e.Graphics.Clear(Color.White); return; }
        GdiPageRenderer.Draw(e.Graphics, _page, _zoom);
        foreach (var it in _highlights)
        {
            bool now = ReferenceEquals(it, _current);
            var box = new RectangleF((float)(it.X * _zoom), (float)(it.Y * _zoom), (float)(it.Width * _zoom), (float)(it.Height * _zoom));
            using var fill = new SolidBrush(now ? Color.FromArgb(110, 255, 140, 0) : Color.FromArgb(90, 255, 235, 0));
            e.Graphics.FillRectangle(fill, box);
            if (now)
            {
                using var edge = new Pen(Color.FromArgb(230, 200, 90, 0), 2f);
                e.Graphics.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
            }
        }
        using var pen = new Pen(Color.FromArgb(120, 120, 120));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
