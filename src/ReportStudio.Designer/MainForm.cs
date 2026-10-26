using System.Text.Json;
using ReportStudio.Core.Data;
using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using ReportStudio.UI;

namespace ReportStudio.Designer;

internal sealed class MainForm : Form
{
    private const string FileFilter = "ReportStudio report (*.rsr)|*.rsr|All files (*.*)|*.*";

    private readonly DesignCanvas _canvas = new();
    private readonly PropertyGrid _props = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = true };
    private readonly ListBox _fields = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ViewerControl _viewer = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Dictionary<DesignTool, ToolStripButton> _toolButtons = new();

    private ReportDefinition _def = SampleReport.Create();
    private string? _path;
    private bool _dirty;
    private bool _dbTrusted = true;
    private Dictionary<string, string?> _lastParameters = new();

    public MainForm(string? fileToOpen)
    {
        Width = 1400;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;

        // WinForms docks the control added LAST first, so the fill area goes in first and the menu last.
        BuildBody();
        Controls.Add(new StatusStrip { Items = { _status } });
        BuildToolbar();
        BuildMenu();

        // Host reports have no data inside the designer: preview them empty so the layout can still be checked.
        _viewer.RowProvider = _ => Array.Empty<IReadOnlyDictionary<string, object?>>();

        _canvas.SelectionChanged += (_, _) => OnSelectionChanged();
        _canvas.DocumentChanged += (_, _) => SetDirty(true);
        _canvas.DefinitionReplaced += (_, _) => { _def = _canvas.Definition; RefreshFields(); };
        _canvas.ToolUsed += (_, _) => HighlightTool();
        _canvas.ImageRequested += PickImage;
        _props.PropertyValueChanged += (_, _) => _canvas.NotifyChanged();
        _fields.DoubleClick += (_, _) => InsertSelectedField();
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == 1) RunPreview(forcePrompt: false); };

        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };

        if (fileToOpen != null && File.Exists(fileToOpen)) OpenFile(fileToOpen);
        else LoadDefinition(_def, null);
    }

    // ================================================================ UI construction

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("&New (blank)", Keys.Control | Keys.N, (_, _) => NewBlank()),
            Item("New from &sample", Keys.None, (_, _) => NewSample()),
            Item("&Open…", Keys.Control | Keys.O, (_, _) => OpenWithDialog()),
            Item("&Save", Keys.Control | Keys.S, (_, _) => Save(false)),
            Item("Save &as…", Keys.Control | Keys.Shift | Keys.S, (_, _) => Save(true)),
            new ToolStripSeparator(),
            Item("E&xit", Keys.None, (_, _) => Close())
        });

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("&Undo\tCtrl+Z", Keys.None, (_, _) => _canvas.Undo()),
            Item("&Redo\tCtrl+Y", Keys.None, (_, _) => _canvas.Redo()),
            new ToolStripSeparator(),
            Item("Cu&t\tCtrl+X", Keys.None, (_, _) => _canvas.Cut()),
            Item("&Copy\tCtrl+C", Keys.None, (_, _) => _canvas.Copy()),
            Item("&Paste\tCtrl+V", Keys.None, (_, _) => _canvas.Paste()),
            Item("&Delete\tDel", Keys.None, (_, _) => _canvas.DeleteSelection()),
            Item("Select &all in band\tCtrl+A", Keys.None, (_, _) => _canvas.SelectAll())
        });

        var report = new ToolStripMenuItem("&Report");
        report.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Report &settings (data, parameters, groups, page)…", Keys.None, (_, _) => EditSettings()),
            Item("&Preview", Keys.F5, (_, _) => { _tabs.SelectedIndex = 1; RunPreview(forcePrompt: true); }),
            Item("Back to &design", Keys.F6, (_, _) => _tabs.SelectedIndex = 0)
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("Expression &reference", Keys.F1, (_, _) => ShowHelp()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, report, help });
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildToolbar()
    {
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };

        bar.Items.Add(Button("Open", (_, _) => OpenWithDialog()));
        bar.Items.Add(Button("Save", (_, _) => Save(false)));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Button("Undo", (_, _) => _canvas.Undo()));
        bar.Items.Add(Button("Redo", (_, _) => _canvas.Redo()));
        bar.Items.Add(new ToolStripSeparator());

        bar.Items.Add(new ToolStripLabel("Insert:"));
        AddToolButton(bar, DesignTool.Select, "Select");
        AddToolButton(bar, DesignTool.Text, "Text / field");
        AddToolButton(bar, DesignTool.Line, "Line");
        AddToolButton(bar, DesignTool.Rectangle, "Box");
        AddToolButton(bar, DesignTool.Image, "Image");
        bar.Items.Add(new ToolStripSeparator());

        bar.Items.Add(Button("B", (_, _) => Toggle(i => i.Bold, (i, v) => i.Bold = v), FontStyle.Bold));
        bar.Items.Add(Button("I", (_, _) => Toggle(i => i.Italic, (i, v) => i.Italic = v), FontStyle.Italic));
        bar.Items.Add(Button("U", (_, _) => Toggle(i => i.Underline, (i, v) => i.Underline = v), FontStyle.Underline));
        bar.Items.Add(Button("Left", (_, _) => _canvas.ApplyToSelection(i => i.HAlign = HAlign.Left)));
        bar.Items.Add(Button("Center", (_, _) => _canvas.ApplyToSelection(i => i.HAlign = HAlign.Center)));
        bar.Items.Add(Button("Right", (_, _) => _canvas.ApplyToSelection(i => i.HAlign = HAlign.Right)));
        bar.Items.Add(new ToolStripSeparator());

        var align = new ToolStripDropDownButton("Align ▾");
        foreach (var (label, kind) in new[]
                 {
                     ("Left edges", AlignKind.Left), ("Right edges", AlignKind.Right), ("Top edges", AlignKind.Top),
                     ("Bottom edges", AlignKind.Bottom), ("Same width", AlignKind.SameWidth), ("Same height", AlignKind.SameHeight)
                 })
            align.DropDownItems.Add(label, null, (_, _) => _canvas.Align(kind));
        bar.Items.Add(align);

        var snap = new ToolStripButton("Snap to grid") { CheckOnClick = true, Checked = true };
        snap.CheckedChanged += (_, _) => _canvas.SnapToGrid = snap.Checked;
        bar.Items.Add(snap);
        bar.Items.Add(new ToolStripSeparator());

        bar.Items.Add(Button("Settings…", (_, _) => EditSettings()));
        bar.Items.Add(Button("▶ Preview", (_, _) => { _tabs.SelectedIndex = 1; RunPreview(forcePrompt: true); }, FontStyle.Bold));

        Controls.Add(bar);
    }

    private void BuildBody()
    {
        // left: fields
        var left = new Panel { Dock = DockStyle.Left, Width = 220 };
        left.Controls.Add(_fields);
        left.Controls.Add(new Label
        {
            Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 6, 0, 0),
            Text = "Fields & parameters\n(double-click to insert)", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        });

        // right: properties
        var right = new Panel { Dock = DockStyle.Right, Width = 320 };
        right.Controls.Add(_props);

        // centre: design / preview
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(226, 229, 234) };
        scroller.Controls.Add(_canvas);
        var designTab = new TabPage("Design") { Padding = Padding.Empty };
        designTab.Controls.Add(scroller);
        var previewTab = new TabPage("Preview");
        previewTab.Controls.Add(_viewer);
        _tabs.TabPages.Add(designTab);
        _tabs.TabPages.Add(previewTab);

        Controls.Add(_tabs);
        Controls.Add(right);
        Controls.Add(left);
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        return item;
    }

    private static ToolStripButton Button(string text, EventHandler onClick, FontStyle style = FontStyle.Regular)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        if (style != FontStyle.Regular) b.Font = new Font(SystemFonts.MessageBoxFont!, style);
        b.Click += onClick;
        return b;
    }

    private void AddToolButton(ToolStrip bar, DesignTool tool, string text)
    {
        var b = Button(text, (_, _) => { _canvas.Tool = tool; HighlightTool(); });
        _toolButtons[tool] = b;
        bar.Items.Add(b);
    }

    private void HighlightTool()
    {
        foreach (var (tool, button) in _toolButtons) button.Checked = tool == _canvas.Tool;
        if (_canvas.Tool != DesignTool.Select) _status.Text = "Click and drag inside a band to draw. Esc cancels.";
    }

    // ================================================================ document handling

    private void LoadDefinition(ReportDefinition def, string? path)
    {
        // A file opened from disk that carries its own query is not run (not even for the field list) until confirmed.
        _dbTrusted = path == null || !def.HasOwnQuery;
        _def = def;
        _path = path;
        _lastParameters = new Dictionary<string, string?>();
        _canvas.Definition = def;
        _tabs.SelectedIndex = 0;
        SetDirty(false);
        RefreshFields();
        OnSelectionChanged();
    }

    private void NewBlank()
    {
        if (!ConfirmDiscard()) return;
        var def = new ReportDefinition { Name = "New report" };
        def.DataSource.Provider = DbProvider.Sample;
        def.Normalize();
        LoadDefinition(def, null);
    }

    private void NewSample()
    {
        if (!ConfirmDiscard()) return;
        LoadDefinition(SampleReport.Create(), null);
    }

    private void OpenWithDialog()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Filter = FileFilter };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        try { LoadDefinition(ReportDefinition.Load(path), path); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not open the report:\n" + ex.Message, "Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool Save(bool saveAs)
    {
        var path = _path;
        if (saveAs || path == null)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = FileFilter, DefaultExt = "rsr", FileName = SafeName(_def.Name) + ".rsr", OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            path = dlg.FileName;
        }

        try
        {
            _def.Save(path);
            _path = path;
            SetDirty(false);
            _status.Text = "Saved " + path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not save:\n" + ex.Message, "Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Save changes to '{_def.Name}'?", "ReportStudio Designer",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => Save(false),
            DialogResult.No => true,
            _ => false
        };
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        Text = $"{_def.Name}{(dirty ? " *" : "")} - ReportStudio Designer";
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "report" : name;
    }

    // ================================================================ editing support

    private void OnSelectionChanged()
    {
        // PropertyGrid treats an array passed to SelectedObject as a single object; multi-select needs SelectedObjects.
        if (_canvas.SelectedObject is object[] many) _props.SelectedObjects = many;
        else _props.SelectedObject = _canvas.SelectedObject;
        var sel = _canvas.Selection;
        _status.Text = sel.Count switch
        {
            0 => _canvas.SelectedBand is { } b ? $"Band: {b.DisplayName}" : "Nothing selected",
            1 => $"{sel[0].Kind}: {sel[0].Name}   X {sel[0].X:0}  Y {sel[0].Y:0}  W {sel[0].Width:0}  H {sel[0].Height:0}",
            _ => $"{sel.Count} elements selected"
        };
    }

    private void Toggle(Func<ReportItem, bool> get, Action<ReportItem, bool> set)
    {
        var sel = _canvas.Selection;
        if (sel.Count == 0) return;
        bool value = !sel.All(get);
        _canvas.ApplyToSelection(i => set(i, value));
    }

    private void RefreshFields()
    {
        _fields.Items.Clear();
        try
        {
            if (_dbTrusted || !_def.HasOwnQuery)
            {
                foreach (var f in DataLoader.GetFieldNames(_def.DataSource))
                    _fields.Items.Add(f.Any(char.IsWhiteSpace) ? $"[{f}]" : f);
            }
            else _status.Text = "This file has its own query, so its fields were not loaded. Preview it and confirm to run it.";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not read fields from the data source: " + ex.Message;
        }
        foreach (var p in _def.Parameters) _fields.Items.Add("@" + p.Name);
        foreach (var s in new[] { "Page", "PageCount", "Now", "RowNumber", "Sum(Amount)", "Count()" }) _fields.Items.Add(s);
    }

    private void InsertSelectedField()
    {
        if (_fields.SelectedItem is not string token) return;
        _tabs.SelectedIndex = 0;
        var item = _canvas.AddField(token);
        if (token == "Page")
        {
            item.Text = "Page {Page} of {PageCount}";
            _canvas.NotifyChanged();
        }
        OnSelectionChanged();
    }

    private void PickImage(ReportItem item)
    {
        using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            item.ImageBase64 = Convert.ToBase64String(File.ReadAllBytes(dlg.FileName));
            _canvas.NotifyChanged();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Load image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void EditSettings()
    {
        using var dlg = new ReportSettingsDialog(_def);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _dbTrusted = true;   // the user just wrote or reviewed the data source themselves
        _canvas.NotifyChanged();
        RefreshFields();
        SetDirty(true);
    }

    private void RunPreview(bool forcePrompt)
    {
        if (_def.HasOwnQuery && !_dbTrusted)
        {
            if (!UntrustedFileGuard.Confirm(this, _def, _path)) { _tabs.SelectedIndex = 0; return; }
            _dbTrusted = true;
            RefreshFields();
        }

        if (_def.Parameters.Count > 0 && (forcePrompt || _lastParameters.Count == 0))
        {
            using var dlg = new ParameterDialog(_def.Parameters, _lastParameters);
            if (dlg.ShowDialog(this) != DialogResult.OK) { _tabs.SelectedIndex = 0; return; }
            _lastParameters = dlg.Values;
        }
        _viewer.Run(_def, _lastParameters, _path == null ? null : Path.GetDirectoryName(_path));
    }

    private void ShowHelp()
    {
        MessageBox.Show(this,
            "Text elements may contain {expressions} mixed with plain text:\n\n" +
            "  {Customer}                      a field (use [Field Name] if it has spaces)\n" +
            "  {@Company}                      a report parameter\n" +
            "  {Page} of {PageCount}           page numbers\n" +
            "  {Sum(Amount)}  {Avg(Price)}     totals for the band's group (whole report in the report footer)\n" +
            "  {Count()}  {Min(x)}  {Max(x)}   more aggregates; also CountDistinct(x)\n" +
            "  {Quantity * UnitPrice}          arithmetic + - * / %\n" +
            "  {IIf(Amount > 1000, 'High', 'Normal')}\n" +
            "  {Format(InvoiceDate, 'dd-MMM-yyyy')}   or set the element's Format property (N2, C, 0.00 ...)\n" +
            "  {Upper(x)} {Lower(x)} {Left(x,3)} {Mid(x,2,4)} {Replace(x,'a','b')} {IsNull(x,'-')}\n\n" +
            "Use {{ and }} for literal braces. 'Visible when' accepts any expression that evaluates to true/false.",
            "Expression reference", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
