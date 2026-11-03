using System.Drawing;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using System.ComponentModel;
using System.Drawing;

namespace ReportStudio.UI;

/// <summary>
/// Report viewer: runs a report definition, shows the pages with zoom and paging, and exports
/// (PDF, Excel, Word, images) or prints. Hosted by both the Designer (Preview tab) and the stand-alone Viewer.
/// </summary>
public sealed class ViewerControl : UserControl
{
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private readonly Panel _scroller = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(94, 98, 104) };
    private readonly PageSurface _surface = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripLabel _pageLabel = new("Page 0 of 0");
    private readonly ToolStripComboBox _zoomBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ToolStripButton _prev = new("◀ Prev"), _next = new("Next ▶");
    private readonly ToolStripDropDownButton _export = new("Export ▾");
    private readonly ToolStripButton _print = new("Print…");
    private readonly ToolStripTextBox _find = new() { Width = 150, ToolTipText = "Search the report (Ctrl+F). Enter = next, Shift+Enter = previous." };
    private readonly ToolStripButton _findPrev = new("▲") { ToolTipText = "Previous match (Shift+F3)" };
    private readonly ToolStripButton _findNext = new("▼") { ToolTipText = "Next match (F3)" };
    private readonly ToolStripButton _matchCase = new("Aa") { CheckOnClick = true, ToolTipText = "Match case" };
    private readonly ToolStripLabel _findCount = new("");
    private List<SearchHit> _hits = new();
    private int _hit = -1;
    private string _searched = "";

    private ReportResult? _result;
    private int _pageIndex;
    private bool _fitWidth = true;

    public ViewerControl()
    {
        Dock = DockStyle.Fill;

        _zoomBox.Items.AddRange(new object[] { "Fit width", "50%", "75%", "100%", "125%", "150%", "200%" });
        _zoomBox.SelectedIndex = 0;
        _zoomBox.SelectedIndexChanged += (_, _) => ApplyZoomChoice();

        _prev.Click += (_, _) => ShowPage(_pageIndex - 1);
        _next.Click += (_, _) => ShowPage(_pageIndex + 1);

        AddExport("PDF document", ".pdf", path => PdfExporter.Export(_result!, path));
        AddExport("Excel workbook", ".xlsx", path => ExcelExporter.Export(_result!, path));
        AddExport("Word document", ".docx", path => WordExporter.Export(_result!, path));
        _export.DropDownItems.Add(new ToolStripSeparator());
        AddImageExport("PNG image", ImageFormatKind.Png);
        AddImageExport("JPEG image", ImageFormatKind.Jpeg);
        AddImageExport("Bitmap image", ImageFormatKind.Bmp);
        AddImageExport("TIFF image", ImageFormatKind.Tiff);

        _print.Click += (_, _) => Guard(() => { if (_result != null) PrintHelper.Print(_result, FindForm()); });

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _prev, _pageLabel, _next, new ToolStripSeparator(),
            new ToolStripLabel("Zoom"), _zoomBox, new ToolStripSeparator(), _export, _print, new ToolStripSeparator(),
            new ToolStripLabel("Find"), _find, _findPrev, _findNext, _matchCase, _findCount
        });
        _find.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Step(e.Shift ? -1 : 1); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { _find.Text = ""; ClearSearch(); e.SuppressKeyPress = true; }
        };
        _find.TextChanged += (_, _) => { if (_find.Text.Length == 0) ClearSearch(); };
        _findNext.Click += (_, _) => Step(1);
        _findPrev.Click += (_, _) => Step(-1);
        _matchCase.CheckedChanged += (_, _) => { if (_find.Text.Length > 0) Step(0); };
        _status.Items.Add(_statusLabel);

        _surface.LinkClicked += (_, item) => OnItemClicked(item);
        _scroller.Controls.Add(_surface);
        _scroller.Resize += (_, _) => { if (_fitWidth) FitWidth(); CenterSurface(); };
        _scroller.Scroll += (_, _) => CenterSurface();

        Controls.Add(_scroller);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        UpdateEnabled();
    }

    public ReportResult? Result => _result;

    /// <summary>
    /// For reports using the Host data source: called with the typed parameter values (decimal, DateTime, bool,
    /// string) after the parameter dialog, and returns the rows. The viewer itself never touches a database.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<IReadOnlyDictionary<string, object?>, IEnumerable<IReadOnlyDictionary<string, object?>>>? RowProvider { get; set; }


    /// <summary>
    /// When true, every report is treated as a Host report: any query or connection string inside the file is ignored
    /// and the rows always come from <see cref="RowProvider"/>. Set this in applications that must never open
    /// a database connection on a file's say-so.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HostOnly { get; set; }

    /// <summary>Culture used for numbers and dates in the report. Defaults to Indian formats (12,34,567.00; dd-MM-yyyy).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public System.Globalization.CultureInfo Culture { get; set; } = ReportStudio.Core.Formatting.IndianFormat.Culture;

    /// <summary>Runs the report and displays it. Errors are reported in the status bar and a message box.</summary>
    public bool Run(ReportDefinition definition, IDictionary<string, string?> parameters, string? baseDirectory)
    {
        _lastRun = (definition, parameters, baseDirectory);
        _toggled.Clear();     // a new run starts with every group as the report designed it
        return RunCore(definition, parameters, baseDirectory);
    }

    private (ReportDefinition Definition, IDictionary<string, string?> Parameters, string? BaseDirectory)? _lastRun;
    private readonly HashSet<string> _toggled = new();

    private bool RunCore(ReportDefinition definition, IDictionary<string, string?> parameters, string? baseDirectory)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            using var measurer = new GdiTextMeasurer();
            var options = new ReportOptions { BaseDirectory = baseDirectory, Measurer = measurer, Culture = Culture, ToggledGroups = new HashSet<string>(_toggled) };
            var def = definition.Clone();
            if (HostOnly)
            {
                // The embedding app owns data access: ignore the file's own data source entirely.
                def.DataSource.Provider = DbProvider.Host;
                def.Normalize();
            }
            if (def.DataSource.Provider == DbProvider.Host)
            {
                if (RowProvider == null)
                    throw new InvalidOperationException(
                        "This report needs data supplied by its host application, which is not connected to this viewer.");
                var typed = ReportEngine.ResolveParameters(def, parameters);
                _result = ReportEngine.Run(def, parameters, RowProvider(typed), options);
            }
            else _result = ReportEngine.Run(def, parameters, options);
            ClearSearch();
            ShowPage(0);
            _statusLabel.Text = $"{_result.RowCount:N0} rows · {_result.Pages.Count} page(s)" +
                                (_result.Warnings.Count > 0 ? $" · {_result.Warnings.Count} warning(s): {_result.Warnings[0]}" : "");
            return true;
        }
        catch (Exception ex)
        {
            _result = null;
            ClearSearch();
            _surface.Page = null;
            _pageLabel.Text = "Page 0 of 0";
            _statusLabel.Text = "Failed: " + ex.Message;
            MessageBox.Show(FindForm(), ex.Message, "Could not run the report", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
            UpdateEnabled();
        }
    }

    // ---- paging & zoom ------------------------------------------------------------------------

    /// <summary>
    /// Raised when the user clicks a drill-through item. Set <see cref="DrillThroughEventArgs.Handled"/> to take over (a host
    /// application can open its own screen); otherwise the viewer opens the report in a new window.
    /// </summary>
    public event EventHandler<DrillThroughEventArgs>? DrillThrough;

    private void OnItemClicked(RenderedItem item)
    {
        if (item.DrillKey.Length > 0) ToggleGroup(item.DrillKey);
        else if (item.DrillReport.Length > 0) OpenDrillThrough(item);
        else FollowLink(item.Link);
    }

    /// <summary>Drill-down: opens or closes one group instance and runs the report again, staying on the same page.</summary>
    private void ToggleGroup(string key)
    {
        if (_lastRun == null) return;
        if (!_toggled.Remove(key)) _toggled.Add(key);

        int page = _pageIndex;
        int scroll = -_scroller.AutoScrollPosition.Y;
        var (definition, parameters, directory) = _lastRun.Value;
        if (!RunCore(definition, parameters, directory)) return;
        ShowPage(page);
        _scroller.AutoScrollPosition = new Point(0, Math.Max(0, scroll));
        CenterSurface();
    }

    /// <summary>Drill-through: the report named by the item, run with the item's parameter values, in a new window.</summary>
    private void OpenDrillThrough(RenderedItem item)
    {
        var args = new DrillThroughEventArgs(item.DrillReport, new Dictionary<string, string>(item.DrillParameters, StringComparer.OrdinalIgnoreCase));
        DrillThrough?.Invoke(this, args);
        if (args.Handled) return;

        var path = DrillTarget.ResolveFile(_lastRun?.BaseDirectory, item.DrillReport);
        if (path == null)
        {
            _statusLabel.Text = "Cannot open '" + item.DrillReport + "': only .rsr reports in this report's folder can be opened.";
            return;
        }
        if (!File.Exists(path)) { _statusLabel.Text = "Report not found: " + item.DrillReport; return; }

        Guard(() =>
        {
            var definition = ReportDefinition.Load(path);
            if (!UntrustedFileGuard.Confirm(FindForm(), definition, path)) return;

            var child = new ViewerControl { Culture = Culture, HostOnly = HostOnly, RowProvider = RowProvider };
            var form = new Form { Text = definition.Name, Width = 1000, Height = 800, StartPosition = FormStartPosition.CenterParent };
            form.Controls.Add(child);
            var values = args.Parameters.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
            form.Shown += (_, _) => child.Run(definition, values, Path.GetDirectoryName(path));
            form.Show(FindForm());
        });
    }

    /// <summary>A click on a link: a bookmark jumps inside the report; an address is opened only after the user agrees.</summary>
    private void FollowLink(string link)
    {
        var (kind, value) = LinkTarget.Parse(link);
        if (kind == LinkKind.Internal && _result != null)
        {
            var target = _result.FindBookmark(value);
            if (target == null) { _statusLabel.Text = "Bookmark '" + value.TrimStart('#') + "' was not found in this report."; return; }
            ShowPage(target.PageIndex);
            _scroller.AutoScrollPosition = new Point(0, (int)Math.Max(0, target.Y * _surface.Zoom));
        }
        else if (kind == LinkKind.External)
        {
            var answer = MessageBox.Show(FindForm(), "Open this address?\n\n" + value, "Open link",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(value) { UseShellExecute = true }));
        }
    }

    // ---- search (requirement 29) --------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { _find.Focus(); _find.SelectAll(); return true; }
        if (keyData == Keys.F3) { Step(1); return true; }
        if (keyData == (Keys.Shift | Keys.F3)) { Step(-1); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Goes to the next (+1) or previous (-1) match; 0 searches again from the page on screen. A changed search text or
    /// option starts a new search, beginning at the page being viewed.
    /// </summary>
    private void Step(int direction)
    {
        if (_result == null || _find.Text.Trim().Length == 0) { ClearSearch(); return; }

        var key = (_matchCase.Checked ? "1" : "0") + _find.Text;
        if (key != _searched || direction == 0)
        {
            _hits = ReportSearch.Find(_result, _find.Text, _matchCase.Checked);
            _searched = key;
            // start at the first match on this page or after it (last one on or before it when going back)
            _hit = direction < 0
                ? _hits.FindLastIndex(h => h.PageIndex <= _pageIndex)
                : _hits.FindIndex(h => h.PageIndex >= _pageIndex);
            if (_hit < 0 && _hits.Count > 0) _hit = direction < 0 ? _hits.Count - 1 : 0;
        }
        else if (_hits.Count > 0)
            _hit = (_hit + direction + _hits.Count) % _hits.Count;

        ShowHit();
    }

    private void ShowHit()
    {
        if (_hits.Count == 0 || _hit < 0)
        {
            _findCount.Text = _searched.Length > 0 ? "not found" : "";
            _surface.SetHighlights(Array.Empty<RenderedItem>(), null);
            return;
        }
        var hit = _hits[_hit];
        if (hit.PageIndex != _pageIndex) ShowPage(hit.PageIndex);
        _surface.SetHighlights(_hits.Where(h => h.PageIndex == hit.PageIndex).Select(h => h.Item).ToList(), hit.Item);
        _scroller.AutoScrollPosition = new Point(0, (int)Math.Max(0, hit.Item.Y * _surface.Zoom - 80));
        CenterSurface();
        _findCount.Text = $"{_hit + 1} of {_hits.Count}";
    }

    private void ClearSearch()
    {
        _hits = new List<SearchHit>();
        _hit = -1;
        _searched = "";
        _findCount.Text = "";
        _surface.SetHighlights(Array.Empty<RenderedItem>(), null);
    }

    private void ShowPage(int index)
    {
        if (_result == null || _result.Pages.Count == 0) { _surface.Page = null; _pageLabel.Text = "Page 0 of 0"; return; }
        _pageIndex = Math.Clamp(index, 0, _result.Pages.Count - 1);
        _surface.Page = _result.Pages[_pageIndex];
        if (_hits.Count > 0)
            _surface.SetHighlights(_hits.Where(h => h.PageIndex == _pageIndex).Select(h => h.Item).ToList(),
                _hit >= 0 && _hits[_hit].PageIndex == _pageIndex ? _hits[_hit].Item : null);
        _pageLabel.Text = $"Page {_pageIndex + 1} of {_result.Pages.Count}";
        if (_fitWidth) FitWidth();
        _scroller.AutoScrollPosition = Point.Empty;
        CenterSurface();
        UpdateEnabled();
    }

    private void ApplyZoomChoice()
    {
        var text = _zoomBox.SelectedItem?.ToString() ?? "";
        _fitWidth = text == "Fit width";
        if (_fitWidth) FitWidth();
        else if (int.TryParse(text.TrimEnd('%'), out var pct)) _surface.Zoom = pct / 100f;
        CenterSurface();
    }

    private void FitWidth()
    {
        if (_surface.Page == null) return;
        int avail = _scroller.ClientSize.Width - 40;
        if (avail > 50) _surface.Zoom = avail / (float)_surface.Page.Width;
    }

    private void CenterSurface()
    {
        int x = Math.Max(20, (_scroller.ClientSize.Width - _surface.Width) / 2);
        _scroller.AutoScrollMinSize = new Size(_surface.Width + 40, _surface.Height + 40);
        _surface.Location = new Point(x + _scroller.AutoScrollPosition.X, 20 + _scroller.AutoScrollPosition.Y);
    }

    private void UpdateEnabled()
    {
        bool has = _result != null && _result.Pages.Count > 0;
        _prev.Enabled = has && _pageIndex > 0;
        _next.Enabled = has && _pageIndex < _result!.Pages.Count - 1;
        _export.Enabled = has;
        _print.Enabled = has;
    }

    // ---- export -------------------------------------------------------------------------------

    private void AddExport(string label, string ext, Action<string> action)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => SaveAndRun(label, ext, action);
        _export.DropDownItems.Add(item);
    }

    private void AddImageExport(string label, ImageFormatKind kind)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => SaveAndRun(label, ImageExporter.Extension(kind), path =>
        {
            var files = ImageExporter.Export(_result!, path, kind);
            _statusLabel.Text = files.Count == 1 ? $"Saved {files[0]}" : $"Saved {files.Count} page images in {Path.GetDirectoryName(files[0])}";
        });
        _export.DropDownItems.Add(item);
    }

    private void SaveAndRun(string label, string ext, Action<string> action)
    {
        if (_result == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = $"{label} (*{ext})|*{ext}",
            FileName = Sanitize(_result.ReportName) + ext,
            DefaultExt = ext.TrimStart('.'),
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        Guard(() =>
        {
            _statusLabel.Text = "";
            action(dlg.FileName);
            if (_statusLabel.Text.Length == 0) _statusLabel.Text = $"Saved {dlg.FileName}";
        });
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // Exporters throw library-specific exceptions (PDFsharp, Open XML); show them rather than crash.
            MessageBox.Show(FindForm(), ex.Message, "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "report" : name;
    }
}

/// <summary>A drill-through click: the report asked for and the parameter values to open it with.</summary>
public sealed class DrillThroughEventArgs : EventArgs
{
    public DrillThroughEventArgs(string report, IReadOnlyDictionary<string, string> parameters)
    {
        Report = report;
        Parameters = parameters;
    }

    public string Report { get; }
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Set to true when the host has opened the report itself; the viewer then does nothing more.</summary>
    public bool Handled { get; set; }
}
