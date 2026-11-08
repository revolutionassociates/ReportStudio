using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;

namespace ReportStudio.Designer;

public enum DesignTool { Select, Text, Line, Rectangle, Image, Barcode, QrCode, SubReport, CrossTab, Contents, Barcode2D, Ellipse }
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
    private Dictionary<ReportItem, Band?> _origBand = new();   // where each dragged item started (it may change band while dragging)
    private BandBox? _createBox;
    private Rectangle _createRect;
    private List<Guide> _guides = new();             // alignment guides shown while dragging
    private Band? _guideBand;                        // the band whose coordinates the horizontal guides use
    private ReportItem? _formatSource;               // the element whose look the format painter copies

    private readonly List<ReportItem> _clipboard = new();
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _lastSnapshot = "";

    // Run-time settings, not designer-serialised properties (analyzer WFO1000).
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DesignTool Tool { get; set; } = DesignTool.Select;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SnapToGrid { get; set; } = true;

    public event EventHandler? SelectionChanged;
    public event EventHandler? DocumentChanged;
    public event EventHandler? DefinitionReplaced;
    public event EventHandler? ToolUsed;
    public event Action<ReportItem>? ImageRequested;

    /// <summary>The mouse position in canvas coordinates, or null when it has left the canvas (the rulers follow it).</summary>
    public event Action<Point?>? PointerMoved;

    // ---- geometry the rulers draw from (canvas pixels @96dpi) ----
    // These are in screen pixels (already multiplied by Zoom), since that is what the rulers draw.
    public int PageOrigin => (int)Math.Round(GutterLeft * _zoom);
    public double PageWidth => _def.Page.Width * _zoom;
    public double MarginLeft => _def.Page.MarginLeft * _zoom;
    public double MarginRight => _def.Page.MarginRight * _zoom;

    /// <summary>Top and height of every band's body, so the vertical ruler can restart at 0 in each band.</summary>
    public IReadOnlyList<(int Top, int Height)> BandSpans =>
        _boxes.Select(b => ((int)Math.Round(b.BodyTop * _zoom), (int)Math.Round(b.BodyHeight * _zoom))).ToList();

    // ---- zoom ----
    private static readonly double[] ZoomSteps = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };
    private double _zoom = 1.0;

    public event EventHandler? ZoomChanged;

    /// <summary>Display scale of the design surface (1 = 100%). Everything inside the canvas stays in report pixels.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Zoom
    {
        get => _zoom;
        set
        {
            value = Math.Clamp(Math.Round(value, 2), ZoomSteps[0], ZoomSteps[^1]);
            if (value == _zoom) return;
            _zoom = value;
            RecalcLayout();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ZoomIn() => Zoom = ZoomSteps.FirstOrDefault(s => s > _zoom + 0.001, ZoomSteps[^1]);
    public void ZoomOut() => Zoom = ZoomSteps.LastOrDefault(s => s < _zoom - 0.001, ZoomSteps[0]);

    /// <summary>Mouse events arrive in screen pixels; the rest of the canvas works in report pixels.</summary>
    private MouseEventArgs ToDesign(MouseEventArgs e) =>
        new(e.Button, e.Clicks, (int)Math.Round(e.X / _zoom), (int)Math.Round(e.Y / _zoom), e.Delta);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            if (e.Delta > 0) ZoomIn(); else if (e.Delta < 0) ZoomOut();
            if (e is HandledMouseEventArgs handled) handled.Handled = true;   // do not also scroll the page
            return;
        }
        base.OnMouseWheel(e);
    }

    public DesignCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(226, 229, 234);
        RecalcLayout();
    }

    // ================================================================ public API

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
    /// <summary>Real rows to show instead of {Field} placeholders (requirement 26). Null = show the design text.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ReportStudio.Core.Layout.DesignPreview? Preview
    {
        get => _preview;
        set { _preview = value; Invalidate(); }
    }
    private ReportStudio.Core.Layout.DesignPreview? _preview;

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
        if (kind is ItemKind.Rectangle or ItemKind.Ellipse) { item.BorderWidth = 1; item.BorderColor = "#555555"; }
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
        // Locked elements stay (and stay selected): unlock them first to delete them.
        var removable = _selection.Where(i => !i.Locked).ToList();
        if (removable.Count == 0) return;
        foreach (var item in removable)
            FindBand(item)?.Items.Remove(item);
        _selection.RemoveAll(i => removable.Contains(i));
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
        var groupIds = new Dictionary<string, string>();      // a pasted group is a new group, not part of the original
        foreach (var src in _clipboard)
        {
            var copy = src.Clone();
            if (!string.IsNullOrEmpty(copy.ItemGroup))
            {
                if (!groupIds.TryGetValue(copy.ItemGroup, out var fresh))
                    groupIds[copy.ItemGroup] = fresh = Guid.NewGuid().ToString("N")[..8];
                copy.ItemGroup = fresh;
            }
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
        foreach (var it in _selection.Skip(1).Where(i => !i.Locked))
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

    // ================================================================ arranging

    public void BringItemsToFront() => Restack(ItemArrange.BringToFront);
    public void SendItemsToBack() => Restack(ItemArrange.SendToBack);
    public void BringItemsForward() => Restack(ItemArrange.BringForward);
    public void SendItemsBackward() => Restack(ItemArrange.SendBackward);

    /// <summary>Changes the stacking order inside every band the selection touches.</summary>
    private void Restack(Action<Band, IReadOnlyCollection<ReportItem>> action)
    {
        if (_selection.Count == 0) return;
        foreach (var band in _def.AllBands().Where(b => b.Items.Any(i => _selection.Contains(i))))
            action(band, _selection);
        Invalidate();
        Commit();
    }

    /// <summary>Groups the selected elements: clicking one selects all, and they move together.</summary>
    public void GroupSelection()
    {
        if (!ItemArrange.Group(_selection)) return;
        Invalidate();
        Commit();
    }

    /// <summary>Releases the selected elements (and so their whole group, which is selected with them) from the group.</summary>
    public void UngroupSelection()
    {
        if (_selection.Count == 0) return;
        ItemArrange.Ungroup(_selection);
        Invalidate();
        Commit();
    }

    /// <summary>Locks the selected elements, or unlocks them when they are all locked already.</summary>
    public void ToggleLock()
    {
        if (_selection.Count == 0) return;
        bool lockThem = !_selection.All(i => i.Locked);
        foreach (var item in _selection) item.Locked = lockThem;
        Invalidate();
        Commit();
        SelectionChanged?.Invoke(this, EventArgs.Empty);      // refreshes the property grid
    }

    /// <summary>True once an element's look has been picked up with <see cref="CopyFormat"/>.</summary>
    public bool HasFormat => _formatSource != null;

    /// <summary>Picks up the look (font, colours, border, alignment, number format) of the first selected element.</summary>
    public void CopyFormat()
    {
        if (_selection.Count > 0) _formatSource = _selection[0].Clone();
    }

    /// <summary>Gives the picked-up look to every selected element.</summary>
    public void PasteFormat()
    {
        if (_formatSource == null || _selection.Count == 0) return;
        foreach (var item in _selection) ItemArrange.CopyAppearance(_formatSource, item);
        Invalidate();
        Commit();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// While dragging: snaps the moved elements to the edges and centres of the other elements (and to the page and band
    /// edges) and remembers the guide lines to draw. Hold Alt to drag freely. Only used when the moved elements share a band.
    /// </summary>
    private void ApplyGuides()
    {
        _guides = new List<Guide>();
        var moved = _selection.Where(i => !i.Locked).ToList();
        if (moved.Count == 0 || (ModifierKeys & Keys.Alt) != 0) return;

        var band = FindBand(moved[0]);
        if (band == null || moved.Any(i => !ReferenceEquals(FindBand(i), band))) return;
        _guideBand = band;

        double left = moved.Min(i => i.X), top = moved.Min(i => i.Y), right = moved.Max(i => i.Right), bottom = moved.Max(i => i.Bottom);
        var box = new Box(left, top, right - left, bottom - top);

        var xTargets = _def.AllBands().SelectMany(b => b.Items).Where(i => !_selection.Contains(i)).Select(Box.Of).ToList();
        xTargets.Add(new Box(0, 0, _def.Page.ContentWidth, 0));          // left edge, centre and right edge of the page content
        var yTargets = band.Items.Where(i => !_selection.Contains(i)).Select(Box.Of).ToList();
        yTargets.Add(new Box(0, 0, 0, band.Height));                     // top, middle and bottom of the band

        var (dx, dy, guides) = ItemArrange.SnapToGuides(box, xTargets, yTargets, Math.Max(1, 4 / _zoom));
        foreach (var item in moved)
        {
            item.X = Math.Max(0, item.X + dx);
            item.Y = Math.Max(0, item.Y + dy);
        }
        _guides = guides;
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
        Size = new Size((int)Math.Ceiling((GutterLeft + (int)_def.Page.Width + 24) * _zoom),
                        (int)Math.Ceiling((y + 48) * _zoom));
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
        g.ScaleTransform((float)_zoom, (float)_zoom);   // everything below is drawn in report pixels

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

        // alignment guides while dragging: vertical across the page, horizontal across the band
        if (_drag == Drag.Move && _guides.Count > 0 && _guideBand != null && BoxOf(_guideBand) is { } guideBox)
        {
            using var pen = new Pen(Color.FromArgb(230, 0, 160), (float)(1 / _zoom)) { DashStyle = DashStyle.Dash };
            foreach (var line in _guides)
            {
                if (line.Vertical)
                    g.DrawLine(pen, ContentLeft + (float)line.Position, 0, ContentLeft + (float)line.Position, totalH);
                else
                    g.DrawLine(pen, PageLeft, guideBox.BodyTop + (float)line.Position, PageLeft + pageW, guideBox.BodyTop + (float)line.Position);
            }
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
            Text = _preview?.TextFor(item) ?? item.Text, FontFamily = item.FontFamily, FontSize = item.FontSize,
            Bold = item.Bold, Italic = item.Italic, Underline = item.Underline,
            HAlign = item.HAlign, VAlign = item.VAlign, WordWrap = item.WordWrap,
            ForeColor = item.ForeColor, BackColor = item.BackColor,
            BorderColor = item.BorderColor, BorderWidth = item.BorderWidth, CornerRadius = item.CornerRadius, TextRotation = item.TextRotation,
            GradientColor = item.GradientColor, GradientDirection = item.GradientDirection,
            Image = DecodeImage(item),
            BarcodeType = item.BarcodeType, QrLevel = item.QrLevel, Barcode2DType = item.Barcode2DType
        };
        // A barcode / QR value written as an expression ({EmpCode}) has no value at design time: show a sample.
        if (_preview == null && item.Kind == ItemKind.Barcode && item.Text.Contains('{')) ri.Text = ReportStudio.Core.Barcodes.BarcodeEncoder.SampleValue(item.BarcodeType);
        if (_preview == null && item.Kind == ItemKind.QrCode && item.Text.Contains('{')) ri.Text = "https://example.com";
        if (_preview == null && item.Kind == ItemKind.Barcode2D && item.Text.Contains('{')) ri.Text = ReportStudio.Core.Barcodes.Symbol2D.SampleValue(item.Barcode2DType);
        if (item.Kind is ItemKind.SubReport or ItemKind.CrossTab or ItemKind.Contents)
        {
            // Their content is only known at run time: show a labelled placeholder.
            using var tint = new SolidBrush(Color.FromArgb(40, 0, 120, 215));
            g.FillRectangle(tint, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
            using var f = new Font("Segoe UI", 8f, FontStyle.Italic);
            var caption = item.Kind == ItemKind.Contents
                ? "[Table of contents: lists every element with an Outline title]"
                : item.Kind == ItemKind.CrossTab
                ? "[Cross-tab: " + (string.IsNullOrWhiteSpace(item.CrossRows) ? "(set rows)" : item.CrossRows + " x " + item.CrossColumns) + "]"
                : "[Sub-report: " + (string.IsNullOrWhiteSpace(item.SubReportPath) ? "(choose a report)" : item.SubReportPath) + "]";
            g.DrawString(caption, f, Brushes.DimGray, r.X + 2, r.Y + 2);
        }
        else GdiPageRenderer.DrawItem(g, ri);

        // faint outline so empty / transparent elements stay visible and grabbable
        using (var outline = new Pen(Color.FromArgb(selected ? 0 : 60, 0, 120, 215)) { DashStyle = DashStyle.Dot })
            g.DrawRectangle(outline, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));

        if (item.Kind == ItemKind.Image && ri.Image == null)
        {
            using var f = new Font("Segoe UI", 8f, FontStyle.Italic);
            g.DrawString("[Image]", f, Brushes.Gray, r.X + 2, r.Y + 2);
        }

        if (item.Locked)
        {
            // a small padlock in the top right corner
            int lx = r.Right - 12, ly = r.Y + 1;
            using var lockPen = new Pen(Color.DimGray, 1.5f);
            g.DrawArc(lockPen, lx + 2, ly, 5, 6, 180, 180);
            g.FillRectangle(Brushes.DimGray, lx, ly + 4, 9, 6);
        }

        if (!selected) return;

        using var pen = new Pen(Color.FromArgb(0, 120, 215), 1.5f);
        g.DrawRectangle(pen, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
        if (_selection.Count == 1 && !item.Locked)
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
        e = ToDesign(e);
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
        if (_selection.Count == 1 && !_selection[0].Locked)
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
            // A grouped element brings its whole group with it.
            var mates = ItemArrange.ExpandGroups(_def.AllBands(), new[] { item });
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (_selection.Contains(item)) _selection.RemoveAll(i => mates.Contains(i));
                else foreach (var mate in mates) if (!_selection.Contains(mate)) _selection.Add(mate);
            }
            else if (!_selection.Contains(item))
            {
                _selection.Clear();
                _selection.AddRange(mates);
            }

            _selectedBand = null;
            if (_selection.Contains(item))
            {
                _drag = Drag.Move;
                _orig = _selection.ToDictionary(i => i, i => (i.X, i.Y, i.Width, i.Height));
                _origBand = _selection.ToDictionary(i => i, i => FindBand(i));
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

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        PointerMoved?.Invoke(null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMoved?.Invoke(e.Location);   // the rulers want screen pixels
        e = ToDesign(e);

        if (_drag == Drag.None) { UpdateCursor(e.Location); return; }

        int dx = e.X - _start.X, dy = e.Y - _start.Y;

        switch (_drag)
        {
            case Drag.Move:
            {
                // The band under the mouse takes the selection; outside every band the items stay where they are.
                var target = BoxAt(e.Location)?.Band;
                foreach (var it in _selection)
                {
                    if (it.Locked) continue;
                    var o = _orig[it];
                    var current = FindBand(it);
                    var dest = target ?? current;
                    var destBox = dest == null ? null : BoxOf(dest);
                    var homeBand = _origBand.TryGetValue(it, out var hb) ? hb : null;
                    var homeBox = homeBand == null ? null : BoxOf(homeBand);

                    double maxX = Math.Max(0, _def.Page.ContentWidth - it.Width);
                    it.X = Math.Clamp(Snap(o.X + dx), 0, maxX);

                    if (dest == null || destBox == null || homeBox == null)
                    {
                        it.Y = Math.Clamp(Snap(o.Y + dy), 0, 1e6);
                        continue;
                    }

                    if (!ReferenceEquals(dest, current))
                    {
                        current?.Items.Remove(it);
                        dest.Items.Add(it);
                    }
                    // keep the item's place on the page: its old canvas position plus the mouse movement
                    double maxY = Math.Max(0, dest.Height - it.Height);
                    it.Y = Math.Clamp(Snap(homeBox.BodyTop + o.Y + dy - destBox.BodyTop), 0, maxY);
                }
                ApplyGuides();
                break;
            }

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
        e = ToDesign(e);
        var drag = _drag;
        _drag = Drag.None;
        _guides = new List<Guide>();

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
                DesignTool.Ellipse => ItemKind.Ellipse,
                DesignTool.Image => ItemKind.Image,
                DesignTool.Barcode => ItemKind.Barcode,
                DesignTool.QrCode => ItemKind.QrCode,
                DesignTool.Barcode2D => ItemKind.Barcode2D,
                DesignTool.SubReport => ItemKind.SubReport,
                DesignTool.CrossTab => ItemKind.CrossTab,
                DesignTool.Contents => ItemKind.Contents,
                _ => ItemKind.Text
            };
            if (w < 8) w = kind == ItemKind.Line ? 120 : kind == ItemKind.Barcode ? 160 : kind == ItemKind.QrCode || kind == ItemKind.Barcode2D ? 80 : kind == ItemKind.SubReport ? 300 : kind == ItemKind.CrossTab ? 400 : kind == ItemKind.Contents ? 400 : 100;
            if (h < 8 && kind != ItemKind.Line)
                h = kind == ItemKind.Image || kind == ItemKind.Ellipse ? 60 : kind == ItemKind.Barcode ? 50 : kind == ItemKind.QrCode || kind == ItemKind.Barcode2D ? 80 : kind == ItemKind.SubReport ? 60 : kind == ItemKind.CrossTab ? 80 : kind == ItemKind.Contents ? 200 : 20;

            var item = AddItem(kind, box.Band, Math.Max(0, Snap(x)), Math.Max(0, Snap(y)), Snap(w), Snap(h),
                kind == ItemKind.Text ? "Text" : kind == ItemKind.Barcode ? "1234567890" : kind == ItemKind.QrCode ? "https://example.com" : kind == ItemKind.Barcode2D ? "ReportStudio 2D" : "");

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

        if (_selection.Count == 1 && !_selection[0].Locked)
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
            case Keys.Control | Keys.Shift | Keys.C: CopyFormat(); return true;
            case Keys.Control | Keys.Shift | Keys.V: PasteFormat(); return true;
            case Keys.Control | Keys.G: GroupSelection(); return true;
            case Keys.Control | Keys.Shift | Keys.G: UngroupSelection(); return true;
            case Keys.Control | Keys.L: ToggleLock(); return true;
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
        foreach (var it in _selection.Where(i => !i.Locked))
        {
            it.X = Math.Max(0, it.X + dx);
            it.Y = Math.Max(0, it.Y + dy);
        }
        Invalidate();
        Commit();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
