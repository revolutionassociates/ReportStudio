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
            new ToolStripLabel("Zoom"), _zoomBox, new ToolStripSeparator(), _export, _print
        });
        _status.Items.Add(_statusLabel);

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
        try
        {
            Cursor = Cursors.WaitCursor;
            using var measurer = new GdiTextMeasurer();
            var options = new ReportOptions { BaseDirectory = baseDirectory, Measurer = measurer, Culture = Culture };
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
            ShowPage(0);
            _statusLabel.Text = $"{_result.RowCount:N0} rows · {_result.Pages.Count} page(s)" +
                                (_result.Warnings.Count > 0 ? $" · {_result.Warnings.Count} warning(s): {_result.Warnings[0]}" : "");
            return true;
        }
        catch (Exception ex)
        {
            _result = null;
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

    private void ShowPage(int index)
    {
        if (_result == null || _result.Pages.Count == 0) { _surface.Page = null; _pageLabel.Text = "Page 0 of 0"; return; }
        _pageIndex = Math.Clamp(index, 0, _result.Pages.Count - 1);
        _surface.Page = _result.Pages[_pageIndex];
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
