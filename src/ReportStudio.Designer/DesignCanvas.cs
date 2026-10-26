using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace ReportStudio.Designer;

public enum DesignTool { Select, Text, Line, Rectangle, Image }
public enum AlignKind { Left, Right, Top, Bottom, SameWidth, SameHeight }

/// <summary>
/// The band-based design surface (the Crystal-Reports-style canvas): bands are stacked top to bottom,
/// elements are dragged, resized and snapped inside them, and every change is undoable.
/// </summary>
public sealed class DesignCanvas : Control
{
    private const int GutterLeft = 24;      // grey area left of the page
    private const int BandHeaderHeight = 18;
    private const int GripSize = 6;
    private const int GridSize = 4;

    private enum Drag { None, Move, Resize, BandResize, Create }
    private enum Grip { None, NW, N, NE, E, SE, S, SW, W }

    private sealed class BandBox
    {
        public Band Band { get; init; } = null!;
        public int HeaderTop { get; init; }
        public int BodyTop { get; init; }
        public int BodyHeight { get; init; }
        public int BodyBottom => BodyTop + BodyHeight;
    }

    private static readonly ConditionalWeakTable<string, byte[]> ImageCache = new();

    private ReportDefinition _def = new();
    private readonly List<BandBox> _boxes = new();
    private readonly List<ReportItem> _selection = new();
    private Band? _selectedBand;

    private Drag _drag = Drag.None;
    private Grip _grip = Grip.None;
    private Point _start;
    private Band? _dragBand;
    private double _origBandHeight;
    private Dictionary<ReportItem, (double X, double Y, double W, double H)> _orig = new();
    private BandBox? _createBox;
    private Rectangle _createRect;

    private readonly List<ReportItem> _clipboard = new();
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _lastSnapshot = "";
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DesignTool Tool { get; set; } = DesignTool.Select;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SnapToGrid { get; set; } = true;

    public event EventHandler? SelectionChanged;
    public event EventHandler? DocumentChanged;
    public event EventHandler? DefinitionReplaced;
    public event EventHandler? ToolUsed;
    public event Action<ReportItem>? ImageRequested;

    public DesignCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(226, 229, 234);
        RecalcLayout();
    }

    // ================================================================ public API

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ReportDefinition Definition
    {
        get => _def;
        set
        {
            _def = value;
            _def.Normalize();
            _selection.Clear();
            _selectedBand = null;
            _undo.Clear();
            _redo.Clear();
            _lastSnapshot = _def.ToJson();
            RecalcLayout();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What the property grid should show: the item(s), else the selected band.</summary>
    public object? SelectedObject =>
        _selection.Count == 1 ? _selection[0]
        : _selection.Count > 1 ? _selection.ToArray()
        : _selectedBand;

    public IReadOnlyList<ReportItem> Selection => _selection;
    public Band? SelectedBand => _selectedBand ?? (_selection.Count > 0 ? FindBand(_selection[0]) : null);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Call after anything outside the canvas changed the definition (property grid, dialogs).</summary>
    public void NotifyChanged()
    {
        RecalcLayout();
        Commit();
    }

    public void Commit()
    {
        var now = _def.ToJson();
        if (now == _lastSnapshot) return;
        _undo.Push(_lastSnapshot);
        _redo.Clear();
        _lastSnapshot = now;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_lastSnapshot);
        _lastSnapshot = _undo.Pop();
        ReplaceFromSnapshot();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_lastSnapshot);
        _lastSnapshot = _redo.Pop();
        ReplaceFromSnapshot();
    }

    private void ReplaceFromSnapshot()
    {
        _def = ReportDefinition.FromJson(_lastSnapshot);
        _selection.Clear();
        _selectedBand = null;
        RecalcLayout();
        DefinitionReplaced?.Invoke(this, EventArgs.Empty);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectBand(Band band)
    {
        _selection.Clear();
        _selectedBand = band;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public ReportItem AddItem(ItemKind kind, Band band, double x, double y, double w, double h, string text = "")
    {
        var item = new ReportItem
        {
            Kind = kind, X = x, Y = y, Width = w, Height = h, Text = text,
            Name = $"{kind}{_def.AllBands().Sum(b => b.Items.Count) + 1}",
            FontFamily = ReportItem.DefaultFontFamily, FontSize = 9
        };
        if (kind == ItemKind.Line) { item.Height = 0; item.BorderWidth = 1; item.ForeColor = "#555555"; }
        if (kind == ItemKind.Rectangle) { item.BorderWidth = 1; item.BorderColor = "#555555"; }
        if (kind == ItemKind.Text) item.VAlign = VAlign.Middle;

        band.Items.Add(item);
        if (item.Bottom > band.Height) band.Height = Math.Ceiling(item.Bottom);
        RecalcLayout();
        SelectOnly(item);
        Commit();
        return item;
    }

    /// <summary>Adds a field expression such as {Customer} to a band (the Detail band by default).</summary>
    public ReportItem AddField(string field, Band? band = null)
    {
        band ??= SelectedBand ?? _def.Detail;
        double x = band.Items.Count == 0 ? 0 : band.Items.Max(i => i.Right) + 6;
        if (x > _def.Page.ContentWidth - 100) x = 0;
        return AddItem(ItemKind.Text, band, x, 1, 100, Math.Max(16, band.Height - 2), "{" + field + "}");
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        foreach (var item in _selection)
            FindBand(item)?.Items.Remove(item);
        _selection.Clear();
        RecalcLayout();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Commit();
    }

    public void Copy()
    {
        _clipboard.Clear();
        _clipboard.AddRange(_selection.Select(i => i.Clone()));
    }

    public void Cut()
    {
        Copy();
        DeleteSelection();
    }

    public void Paste()
    {
        if (_clipboard.Count == 0) return;
        var band = SelectedBand ?? _def.Detail;
        _selection.Clear();
        foreach (var src in _clipboard)
        {
            var copy = src.Clone();
            copy.X += 8; copy.Y += 8;
            band.Items.Add(copy);
            _selection.Add(copy);
        }
        // Keep a second paste from landing exactly on top of the first.
        foreach (var c in _clipboard) { c.X += 8; c.Y += 8; }
        _selectedBand = null;
        RecalcLayout();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Commit();
    }

    public void SelectAll()
    {
        var band = SelectedBand ?? _def.Detail;
        _selection.Clear();
        _selection.AddRange(band.Items);
        _selectedBand = null;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Align(AlignKind kind)
    {
        if (_selection.Count < 2) return;
        var first = _selection[0];
        foreach (var it in _selection.Skip(1))
        {
            switch (kind)
            {
                case AlignKind.Left: it.X = first.X; break;
                case AlignKind.Right: it.X = first.Right - it.Width; break;
                case AlignKind.Top: it.Y = first.Y; break;
                case AlignKind.Bottom: it.Y = first.Bottom - it.Height; break;
                case AlignKind.SameWidth: it.Width = first.Width; break;
                case AlignKind.SameHeight: it.Height = first.Height; break;
            }
        }
        RecalcLayout();
        Commit();
    }

    /// <summary>Applies a formatting change (bold, alignment...) to every selected text element.</summary>
    public void ApplyToSelection(Action<ReportItem> apply)
    {
        foreach (var it in _selection) apply(it);
        Invalidate();
        Commit();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================ layout

    private void RecalcLayout()
    {
        _boxes.Clear();
        int y = 0;
        foreach (var band in _def.AllBands())
        {
            int h = Math.Max(1, (int)Math.Round(band.Height));
            _boxes.Add(new BandBox { Band = band, HeaderTop = y, BodyTop = y + BandHeaderHeight, BodyHeight = h });
            y += BandHeaderHeight + h;
        }
        Size = new Size(GutterLeft + (int)_def.Page.Width + 24, y + 48);
        Invalidate();
    }

    private int PageLeft => GutterLeft;
    private int ContentLeft => GutterLeft + (int)_def.Page.MarginLeft;

    private Rectangle ItemRect(ReportItem it, BandBox box) =>
        new(ContentLeft + (int)Math.Round(it.X), box.BodyTop + (int)Math.Round(it.Y),
            Math.Max(1, (int)Math.Round(it.Width)), Math.Max(1, (int)Math.Round(it.Height)));

    private BandBox? BoxOf(Band band) => _boxes.FirstOrDefault(b => ReferenceEquals(b.Band, band));

    private Band? FindBand(ReportItem item) => _def.AllBands().FirstOrDefault(b => b.Items.Contains(item));

    private BandBox? BoxAt(Point p) =>
        _boxes.FirstOrDefault(b => p.Y >= b.HeaderTop && p.Y <= b.BodyBottom);

    private double Snap(double v) => SnapToGrid ? Math.Round(v / GridSize) * GridSize : Math.Round(v);

    // ================================================================ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int pageW = (int)_def.Page.Width;
        int totalH = _boxes.Count == 0 ? Height : _boxes[^1].BodyBottom;

        using (var paper = new SolidBrush(Color.White))
            g.FillRectangle(paper, PageLeft, 0, pageW, totalH);

        using (var guide = new Pen(Color.FromArgb(215, 220, 228)) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(guide, ContentLeft, 0, ContentLeft, totalH);
            int right = PageLeft + pageW - (int)_def.Page.MarginRight;
            g.DrawLine(guide, right, 0, right, totalH);
        }

        foreach (var box in _boxes)
            DrawBand(g, box, pageW);

        if (_drag == Drag.Create)
        {
            using var pen = new Pen(Color.FromArgb(0, 120, 215)) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, _createRect);
        }
    }

    private void DrawBand(Graphics g, BandBox box, int pageW)
    {
        bool bandSelected = ReferenceEquals(_selectedBand, box.Band);

        var header = new Rectangle(PageLeft, box.HeaderTop, pageW, BandHeaderHeight);
        using (var hb = new SolidBrush(bandSelected ? Color.FromArgb(150, 180, 220) : Color.FromArgb(206, 216, 232)))
            g.FillRectangle(hb, header);
        using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
        using (var tb = new SolidBrush(Color.FromArgb(40, 55, 80)))
            g.DrawString($"{box.Band.DisplayName}  ({box.Band.Height:0} px)", f, tb, PageLeft + 4, box.HeaderTop + 2);

        using (var edge = new Pen(Color.FromArgb(120, 140, 175)) { DashStyle = DashStyle.Dash })
            g.DrawLine(edge, PageLeft, box.BodyBottom, PageLeft + pageW, box.BodyBottom);

        foreach (var item in box.Band.Items)
            DrawItem(g, item, box);
    }

    private void DrawItem(Graphics g, ReportItem item, BandBox box)
    {
        var r = ItemRect(item, box);
        bool selected = _selection.Contains(item);

        var ri = new RenderedItem
        {
            Kind = item.Kind, X = r.X, Y = r.Y, Width = item.Width, Height = item.Height,
            Text = item.Text, FontFamily = item.FontFamily, FontSize = item.FontSize,
            Bold = item.Bold, Italic = item.Italic, Underline = item.Underline,
            HAlign = item.HAlign, VAlign = item.VAlign, WordWrap = item.WordWrap,
            ForeColor = item.ForeColor, BackColor = item.BackColor,
            BorderColor = item.BorderColor, BorderWidth = item.BorderWidth,
            Image = DecodeImage(item)
        };
        GdiPageRenderer.DrawItem(g, ri);

        // faint outline so empty / transparent elements stay visible and grabbable
        using (var outline = new Pen(Color.FromArgb(selected ? 0 : 60, 0, 120, 215)) { DashStyle = DashStyle.Dot })
            g.DrawRectangle(outline, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));

        if (item.Kind == ItemKind.Image && ri.Image == null)
        {
            using var f = new Font("Segoe UI", 8f, FontStyle.Italic);
            g.DrawString("[Image]", f, Brushes.Gray, r.X + 2, r.Y + 2);
        }

        if (!selected) return;

        using var pen = new Pen(Color.FromArgb(0, 120, 215), 1.5f);
        g.DrawRectangle(pen, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
        if (_selection.Count == 1)
            foreach (var hr in GripRects(r).Values)
            {
                g.FillRectangle(Brushes.White, hr);
                g.DrawRectangle(Pens.DodgerBlue, hr);
            }
    }

    private static byte[]? DecodeImage(ReportItem item)
    {
        if (string.IsNullOrEmpty(item.ImageBase64)) return null;
        if (ImageCache.TryGetValue(item.ImageBase64, out var cached)) return cached;
        try
        {
            var bytes = Convert.FromBase64String(item.ImageBase64);
            ImageCache.Add(item.ImageBase64, bytes);
            return bytes;
        }
        catch (FormatException) { return null; }
    }

    private static Dictionary<Grip, Rectangle> GripRects(Rectangle r)
    {
        int s = GripSize, h = s / 2;
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        Rectangle At(int x, int y) => new(x - h, y - h, s, s);
        return new Dictionary<Grip, Rectangle>
        {
            [Grip.NW] = At(r.Left, r.Top), [Grip.N] = At(cx, r.Top), [Grip.NE] = At(r.Right, r.Top),
            [Grip.E] = At(r.Right, cy), [Grip.SE] = At(r.Right, r.Bottom), [Grip.S] = At(cx, r.Bottom),
            [Grip.SW] = At(r.Left, r.Bottom), [Grip.W] = At(r.Left, cy)
        };
    }

    // ================================================================ mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _start = e.Location;

        var box = BoxAt(e.Location);

        // drawing a new element
        if (Tool != DesignTool.Select)
        {
            if (box != null && e.Y >= box.BodyTop)
            {
                _drag = Drag.Create;
                _createBox = box;
                _createRect = new Rectangle(e.Location, Size.Empty);
            }
            return;
        }

        // resize grip of the single selected item
        if (_selection.Count == 1)
        {
            var band = FindBand(_selection[0]);
            var owner = band == null ? null : BoxOf(band);
            if (owner != null)
            {
                var hit = HitGrip(ItemRect(_selection[0], owner), e.Location);
                if (hit != Grip.None) { BeginResize(hit); return; }
            }
        }

        // drag the bottom edge of a band to change its height
        foreach (var b in _boxes)
            if (Math.Abs(e.Y - b.BodyBottom) <= 3 && e.X >= PageLeft && e.X <= PageLeft + _def.Page.Width)
            {
                _drag = Drag.BandResize;
                _dragBand = b.Band;
                _origBandHeight = b.Band.Height;
                SelectBand(b.Band);
                return;
            }

        // click on an element
        var item = HitItem(e.Location);
        if (item != null)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!_selection.Remove(item)) _selection.Add(item);
            }
            else if (!_selection.Contains(item)) _selection.Clear();

            if (!_selection.Contains(item) && (ModifierKeys & Keys.Control) == 0) _selection.Add(item);

            _selectedBand = null;
            if (_selection.Contains(item))
            {
                _drag = Drag.Move;
                _orig = _selection.ToDictionary(i => i, i => (i.X, i.Y, i.Width, i.Height));
            }
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // click on empty space: select the band
        if (box != null) SelectBand(box.Band);
        else
        {
            _selection.Clear();
            _selectedBand = null;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_drag == Drag.None) { UpdateCursor(e.Location); return; }

        int dx = e.X - _start.X, dy = e.Y - _start.Y;

        switch (_drag)
        {
            case Drag.Move:
                foreach (var it in _selection)
                {
                    var o = _orig[it];
                    var band = FindBand(it);
                    double maxX = Math.Max(0, _def.Page.ContentWidth - it.Width);
                    double maxY = band == null ? 1e6 : Math.Max(0, band.Height - it.Height);
                    it.X = Math.Clamp(Snap(o.X + dx), 0, maxX);
                    it.Y = Math.Clamp(Snap(o.Y + dy), 0, maxY);
                }
                break;

            case Drag.Resize:
                ApplyResize(dx, dy);
                break;

            case Drag.BandResize:
                if (_dragBand != null)
                {
                    _dragBand.Height = Math.Max(8, Snap(_origBandHeight + dy));
                    RecalcLayout();
                }
                break;

            case Drag.Create:
                _createRect = Rectangle.FromLTRB(
                    Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                    Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
                break;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var drag = _drag;
        _drag = Drag.None;

        if (drag == Drag.Create && _createBox != null)
        {
            var box = _createBox;
            _createBox = null;
            double x = _createRect.X - ContentLeft;
            double y = _createRect.Y - box.BodyTop;
            double w = _createRect.Width, h = _createRect.Height;

            var kind = Tool switch
            {
                DesignTool.Line => ItemKind.Line,
                DesignTool.Rectangle => ItemKind.Rectangle,
                DesignTool.Image => ItemKind.Image,
                _ => ItemKind.Text
            };
            if (w < 8) w = kind == ItemKind.Line ? 120 : 100;
            if (h < 8 && kind != ItemKind.Line) h = kind == ItemKind.Image ? 60 : 20;

            var item = AddItem(kind, box.Band, Math.Max(0, Snap(x)), Math.Max(0, Snap(y)), Snap(w), Snap(h),
                kind == ItemKind.Text ? "Text" : "");

            Tool = DesignTool.Select;
            ToolUsed?.Invoke(this, EventArgs.Empty);
            if (kind == ItemKind.Image) ImageRequested?.Invoke(item);
            return;
        }

        if (drag is Drag.Move or Drag.Resize or Drag.BandResize)
        {
            Commit();
            SelectionChanged?.Invoke(this, EventArgs.Empty);   // refreshes the property grid values
        }
        Invalidate();
    }

    private void BeginResize(Grip grip)
    {
        _drag = Drag.Resize;
        _grip = grip;
        var it = _selection[0];
        _orig = new Dictionary<ReportItem, (double X, double Y, double W, double H)>
        {
            [it] = (it.X, it.Y, it.Width, it.Height)
        };
    }

    private void ApplyResize(int dx, int dy)
    {
        var it = _selection[0];
        var o = _orig[it];
        double minW = 4, minH = it.Kind == ItemKind.Line ? 0 : 4;
        double x = o.X, y = o.Y, w = o.W, h = o.H;
        double right = o.X + o.W, bottom = o.Y + o.H;

        if (_grip is Grip.NW or Grip.W or Grip.SW)
        {
            x = Math.Min(Snap(o.X + dx), right - minW);
            w = right - x;
        }
        if (_grip is Grip.NE or Grip.E or Grip.SE) w = Math.Max(minW, Snap(o.W + dx));
        if (_grip is Grip.NW or Grip.N or Grip.NE)
        {
            y = Math.Min(Snap(o.Y + dy), bottom - minH);
            h = bottom - y;
        }
        if (_grip is Grip.SW or Grip.S or Grip.SE) h = Math.Max(minH, Snap(o.H + dy));

        if (it.Kind == ItemKind.Line) h = 0;
        it.X = Math.Max(0, x);
        it.Y = Math.Max(0, y);
        it.Width = w;
        it.Height = h;
    }

    private static Grip HitGrip(Rectangle itemRect, Point p)
    {
        foreach (var (grip, rect) in GripRects(itemRect))
            if (Rectangle.Inflate(rect, 3, 3).Contains(p)) return grip;
        return Grip.None;
    }

    private ReportItem? HitItem(Point p)
    {
        foreach (var box in _boxes)
        {
            if (p.Y < box.BodyTop - 2 || p.Y > box.BodyBottom + 2) continue;
            for (int i = box.Band.Items.Count - 1; i >= 0; i--)
            {
                var it = box.Band.Items[i];
                var r = ItemRect(it, box);
                if (it.Kind == ItemKind.Line) r = Rectangle.Inflate(r, 2, 3);
                if (r.Contains(p)) return it;
            }
        }
        return null;
    }

    private void UpdateCursor(Point p)
    {
        if (Tool != DesignTool.Select) { Cursor = Cursors.Cross; return; }

        if (_selection.Count == 1)
        {
            var band = FindBand(_selection[0]);
            var owner = band == null ? null : BoxOf(band);
            if (owner != null)
            {
                var cursor = HitGrip(ItemRect(_selection[0], owner), p) switch
                {
                    Grip.NW or Grip.SE => Cursors.SizeNWSE,
                    Grip.NE or Grip.SW => Cursors.SizeNESW,
                    Grip.N or Grip.S => Cursors.SizeNS,
                    Grip.E or Grip.W => Cursors.SizeWE,
                    _ => null
                };
                if (cursor != null) { Cursor = cursor; return; }
            }
        }

        if (_boxes.Any(b => Math.Abs(p.Y - b.BodyBottom) <= 3 && p.X >= PageLeft && p.X <= PageLeft + _def.Page.Width))
        {
            Cursor = Cursors.SizeNS;
            return;
        }

        Cursor = HitItem(p) != null ? Cursors.SizeAll : Cursors.Default;
    }

    private void SelectOnly(ReportItem item)
    {
        _selection.Clear();
        _selection.Add(item);
        _selectedBand = null;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================ keyboard

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Delete: DeleteSelection(); return true;
            case Keys.Escape: Tool = DesignTool.Select; ToolUsed?.Invoke(this, EventArgs.Empty); return true;
            case Keys.Control | Keys.C: Copy(); return true;
            case Keys.Control | Keys.X: Cut(); return true;
            case Keys.Control | Keys.V: Paste(); return true;
            case Keys.Control | Keys.A: SelectAll(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y: Redo(); return true;
        }

        int step = (keyData & Keys.Shift) != 0 ? GridSize * 2 : 1;
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left: Nudge(-step, 0); return true;
            case Keys.Right: Nudge(step, 0); return true;
            case Keys.Up: Nudge(0, -step); return true;
            case Keys.Down: Nudge(0, step); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Nudge(int dx, int dy)
    {
        if (_selection.Count == 0) return;
        foreach (var it in _selection)
        {
            it.X = Math.Max(0, it.X + dx);
            it.Y = Math.Max(0, it.Y + dy);
        }
        Invalidate();
        Commit();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
