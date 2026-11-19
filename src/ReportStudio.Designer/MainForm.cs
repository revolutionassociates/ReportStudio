using System.Text.Json;
using ReportStudio.Core.Data;
using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using ReportStudio.Core.Ui;
using ReportStudio.Core.Wizard;
using ReportStudio.UI;

namespace ReportStudio.Designer;

internal sealed class MainForm : Form
{
    private const string FileFilter = "ReportStudio report (*.rsr)|*.rsr|All files (*.*)|*.*";

    private readonly DesignCanvas _canvas = new();
    private readonly PropertyGrid _props = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = true };
    private readonly ListBox _fields = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ViewerControl _viewer = new() { AllowFileSources = true, AllowWebSources = true };   // the author's own tool: the trust prompt still applies
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Dictionary<DesignTool, ToolStripButton> _toolButtons = new();
    private RulerControl[] _rulers = Array.Empty<RulerControl>();

    private ReportDefinition _def = SampleReport.Create();
    private string? _path;

    /// <summary>The folder of the open report file (null for a report not yet saved): a data file named without a folder is looked up here.</summary>
    private string? ReportFolder => _path == null ? null : Path.GetDirectoryName(_path);

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

        DesignerUi.Apply(this, translateTitle: false);   // colour theme and language (requirement 47); the title shows the file name

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
            Item("New from &template…", Keys.Control | Keys.Shift | Keys.N, (_, _) => NewFromTemplate()),
            Item("New with &wizard…", Keys.None, (_, _) => NewWithWizard()),
            Item("&Open…", Keys.Control | Keys.O, (_, _) => OpenWithDialog()),
            Item("&Save", Keys.Control | Keys.S, (_, _) => Save(false)),
            Item("Save &as…", Keys.Control | Keys.Shift | Keys.S, (_, _) => Save(true)),
            Item("Save as te&mplate…", Keys.None, (_, _) => SaveAsTemplate()),
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

        var arrange = new ToolStripMenuItem("&Arrange");
        arrange.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Bring to &front", Keys.None, (_, _) => _canvas.BringItemsToFront()),
            Item("Send to &back", Keys.None, (_, _) => _canvas.SendItemsToBack()),
            Item("Bring f&orward", Keys.None, (_, _) => _canvas.BringItemsForward()),
            Item("Send backwar&d", Keys.None, (_, _) => _canvas.SendItemsBackward()),
            new ToolStripSeparator(),
            Item("&Group\tCtrl+G", Keys.None, (_, _) => _canvas.GroupSelection()),
            Item("&Ungroup\tCtrl+Shift+G", Keys.None, (_, _) => _canvas.UngroupSelection()),
            Item("&Lock / unlock\tCtrl+L", Keys.None, (_, _) => _canvas.ToggleLock()),
            new ToolStripSeparator(),
            Item("&Copy format\tCtrl+Shift+C", Keys.None, (_, _) => _canvas.CopyFormat()),
            Item("&Paste format\tCtrl+Shift+V", Keys.None, (_, _) => _canvas.PasteFormat())
        });

        var themeMenu = new ToolStripMenuItem("Apply style &theme");
        foreach (var theme in StyleThemes.All)
            themeMenu.DropDownItems.Add(Item($"{theme.Name} — {theme.Description}", Keys.None, (_, _) => ApplyTheme(theme)));

        var report = new ToolStripMenuItem("&Report");
        report.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Report &settings (data, parameters, groups, page)…", Keys.None, (_, _) => EditSettings()),
            Item("New &style from selected element…", Keys.None, (_, _) => NewStyleFromSelection()),
            themeMenu,
            new ToolStripSeparator(),
            Item("&Preview", Keys.F5, (_, _) => { _tabs.SelectedIndex = 1; RunPreview(forcePrompt: true); }),
            Item("&Live data on the design", Keys.None, (_, _) => ToggleLiveData()),
            Item("Back to &design", Keys.F6, (_, _) => _tabs.SelectedIndex = 0)
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("Expression &reference", Keys.F1, (_, _) => ShowHelp()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, arrange, report, BuildViewMenu(), help });
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    // ---- colour theme and language (requirement 47) ----

    private ToolStripMenuItem BuildViewMenu()
    {
        var view = new ToolStripMenuItem("&View");

        var theme = new ToolStripMenuItem("&Colour theme");
        var themeItems = new Dictionary<string, ToolStripMenuItem>();
        foreach (var (key, label) in new[]
                 {
                     (DesignerSettings.ThemeSystem, "Same as &Windows"),
                     (DesignerSettings.ThemeLight, "&Light"),
                     (DesignerSettings.ThemeDark, "&Dark")
                 })
        {
            var chosen = key;
            var entry = Item(label, Keys.None, (_, _) => DesignerUi.SetTheme(chosen));
            themeItems[key] = entry;
            theme.DropDownItems.Add(entry);
        }
        theme.DropDownOpening += (_, _) =>
        {
            var current = DesignerSettings.NormalizeTheme(DesignerUi.Settings.Theme);
            foreach (var (key, entry) in themeItems) entry.Checked = key == current;
        };

        var language = new ToolStripMenuItem("&Language");
        language.DropDownItems.Add(new ToolStripMenuItem("English"));   // so the arrow shows before the list is filled
        language.DropDownOpening += (_, _) => FillLanguageMenu(language);

        var ribbon = new ToolStripMenuItem("&Ribbon tabs") { CheckOnClick = true, Checked = DesignerUi.Settings.Ribbon };
        ribbon.CheckedChanged += (_, _) => SetRibbon(ribbon.Checked);

        view.DropDownItems.AddRange(new ToolStripItem[] { theme, language, new ToolStripSeparator(), ribbon });
        return view;
    }

    private void FillLanguageMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        foreach (var language in DesignerUi.Languages)
        {
            var code = language.Code;
            var entry = Item(language.Name, Keys.None, (_, _) => DesignerUi.SetLanguage(code));
            entry.Tag = DesignerUi.NoTranslate;   // a language is shown by its own name
            entry.Checked = string.Equals(code, DesignerUi.Language.Code, StringComparison.OrdinalIgnoreCase);
            menu.DropDownItems.Add(entry);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Item("Open the &languages folder", Keys.None, (_, _) => OpenLanguageFolder()));
        menu.DropDownItems.Add(Item("&Export a file for translators…", Keys.None, (_, _) => ExportLanguageFile()));
        menu.DropDownItems.Add(Item("&Check the current language", Keys.None, (_, _) => CheckLanguage()));
        DesignerUi.Apply(menu.DropDown);
    }

    private void OpenLanguageFolder()
    {
        try
        {
            Directory.CreateDirectory(DesignerSettings.LanguageFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DesignerSettings.LanguageFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "The languages folder could not be opened: " + ex.Message + Environment.NewLine + DesignerSettings.LanguageFolder,
                "Languages", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Writes a language file with every text of the windows shown so far in this run and an empty translation for each, for a translator
    /// to fill in. A language that is installed already keeps its translations.
    /// </summary>
    private void ExportLanguageFile()
    {
        using var askCode = new NamePromptDialog("Export a file for translators", "Language code (for example hi or pt-BR):",
            DesignerUi.Language.Code == UiLanguage.English.Code ? "" : DesignerUi.Language.Code);
        if (askCode.ShowDialog(this) != DialogResult.OK) return;
        var code = askCode.EnteredText().Trim();
        if (!UiLanguage.IsValidCode(code) || string.Equals(code, UiLanguage.English.Code, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Write the language code as two or three letters, for example hi, or pt-BR. English (en) is built in.",
                "Export a file for translators", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var existing = DesignerUi.Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        using var askName = new NamePromptDialog("Export a file for translators", "Language name, as its speakers write it:", existing?.Name ?? "");
        if (askName.ShowDialog(this) != DialogResult.OK) return;
        var name = askName.EnteredText().Trim();

        try
        {
            Directory.CreateDirectory(DesignerSettings.LanguageFolder);
            using var save = new SaveFileDialog
            {
                InitialDirectory = DesignerSettings.LanguageFolder,
                FileName = code + ".json",
                Filter = "Language file (*.json)|*.json",
                OverwritePrompt = true
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(save.FileName, UiLanguage.TemplateJson(code, name.Length == 0 ? code : name, DesignerUi.SeenTexts, existing));
            _status.Text = $"Wrote {DesignerUi.SeenTexts.Count} texts to {save.FileName}. Fill in the translations, keep it in the languages folder, then choose the language in View > Language.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The file could not be written: " + ex.Message, "Export a file for translators", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CheckLanguage()
    {
        var language = DesignerUi.Language;
        if (language.Code == UiLanguage.English.Code)
        {
            MessageBox.Show(this, "English is built in and has nothing to check. Choose another language first.", "Languages",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var seen = DesignerUi.SeenTexts;
        var problems = language.Problems(seen);
        int done = seen.Count(t => language.Texts.ContainsKey(t));
        var text = $"{language.Name}: {done} of the {seen.Count} texts of the windows shown so far are translated (open the dialogs to include theirs)." +
                   (problems.Count == 0 ? Environment.NewLine + "No problems found." : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(15)));
        MessageBox.Show(this, text, "Languages", MessageBoxButtons.OK, problems.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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
        AddToolButton(bar, DesignTool.Ellipse, "Ellipse");
        AddToolButton(bar, DesignTool.Image, "Image");
        AddToolButton(bar, DesignTool.Barcode, "Barcode");
        AddToolButton(bar, DesignTool.QrCode, "QR code");
        AddToolButton(bar, DesignTool.Barcode2D, "2D code");
        AddToolButton(bar, DesignTool.SubReport, "Sub-report");
        AddToolButton(bar, DesignTool.CrossTab, "Cross-tab");
        AddToolButton(bar, DesignTool.Contents, "Contents");
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

        // Click to cycle the ruler units: cm -> in -> px
        var unit = new ToolStripButton("Ruler: cm") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        unit.Click += (_, _) =>
        {
            var next = (RulerUnit)(((int)_rulers[0].Unit + 1) % 3);
            foreach (var ruler in _rulers) ruler.Unit = next;
            unit.Text = "Ruler: " + RulerControl.UnitName(next);
        };
        bar.Items.Add(unit);

        // Zoom: - / % (click = back to 100%) / +; Ctrl + mouse wheel also zooms
        var zoomLabel = Button("100%", (_, _) => _canvas.Zoom = 1.0);
        bar.Items.Add(Button("Zoom −", (_, _) => _canvas.ZoomOut()));
        bar.Items.Add(zoomLabel);
        bar.Items.Add(Button("+", (_, _) => _canvas.ZoomIn()));
        _canvas.ZoomChanged += (_, _) => zoomLabel.Text = $"{Math.Round(_canvas.Zoom * 100)}%";
        bar.Items.Add(new ToolStripSeparator());

        bar.Items.Add(Button("Settings…", (_, _) => EditSettings()));
        _liveData = Button("Live data", (_, _) => ToggleLiveData());
        bar.Items.Add(_liveData);
        _nextRow = Button("Next row ▶", (_, _) => { _canvas.Preview?.Next(); _canvas.Invalidate(); UpdateLiveDataText(); });
        _nextRow.Visible = false;
        bar.Items.Add(_nextRow);
        bar.Items.Add(Button("▶ Preview", (_, _) => { _tabs.SelectedIndex = 1; RunPreview(forcePrompt: true); }, FontStyle.Bold));

        // Cut the toolbar into groups at its separators (a separator belongs to the group before it), for the ribbon tabs.
        _bar = bar;
        int group = 0;
        foreach (ToolStripItem item in bar.Items)
        {
            _barGroup[item] = group;
            if (item is ToolStripSeparator) group++;
        }

        Controls.Add(bar);

        // The ribbon tabs sit above the toolbar and below the menu (the menu is added later, so it docks first).
        _ribbonTabs = new TabControl { Dock = DockStyle.Top, Height = 28, Visible = DesignerUi.Settings.Ribbon, Padding = new Point(14, 4) };
        foreach (var name in RibbonNames) _ribbonTabs.TabPages.Add(name);
        _ribbonTabs.SelectedIndexChanged += (_, _) => ApplyRibbon();
        Controls.Add(_ribbonTabs);
        ApplyRibbon();
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

        // Rulers: docking processes the control added last first, so the fill area goes in first,
        // then the left ruler, then the top one (which therefore spans the full width).
        _rulers = new[] { new RulerControl(_canvas, horizontal: true), new RulerControl(_canvas, horizontal: false) };
        var designArea = new Panel { Dock = DockStyle.Fill };
        designArea.Controls.Add(scroller);
        designArea.Controls.Add(_rulers[1]);
        designArea.Controls.Add(_rulers[0]);

        var designTab = new TabPage("Design") { Padding = Padding.Empty };
        designTab.Controls.Add(designArea);
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
        SetLiveData(null);
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

    private void NewFromTemplate()
    {
        using var dlg = new NewFromTemplateDialog(UserTemplates.DefaultFolder);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var template = dlg.SelectedTemplate();
        if (template == null) return;

        ReportDefinition created;
        try { created = template.Create(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not open the template:\n" + ex.Message, "New from template", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        // Asked after the choice, so cancelling the dialog never costs the open report.
        if (!ConfirmDiscard()) return;
        LoadDefinition(created, null);
    }

    private void SaveAsTemplate()
    {
        // A template is trusted when it is opened again, so a query from a file that was never confirmed cannot become one.
        if (!_dbTrusted)
        {
            MessageBox.Show(this, "This report carries a query from a file that has not been confirmed yet.\n" +
                "Run the preview once and confirm it before saving the report as a template.",
                "Save as template", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var ask = new NamePromptDialog("Save as template", "Template name:", _def.Name);
        if (ask.ShowDialog(this) != DialogResult.OK) return;
        var name = ask.EnteredText();
        var folder = UserTemplates.DefaultFolder;
        try
        {
            if (UserTemplates.Exists(folder, name) &&
                MessageBox.Show(this, $"A template named \"{name}\" already exists. Replace it?", "Save as template",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            UserTemplates.Save(folder, _def, name);
            MessageBox.Show(this, $"Saved as template \"{name}\".\nFind it under File > New from template > My templates.",
                "Save as template", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not save the template:\n" + ex.Message, "Save as template", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void NewWithWizard()
    {
        // The wizard starts from the data of the open report; a query that came from an unconfirmed file is not carried over.
        var source = _dbTrusted
            ? ReportDefinition.FromJson(_def.ToJson()).DataSource
            : new DataSourceDefinition { Provider = DbProvider.Sample };
        using var dlg = new ReportWizardDialog(source);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var created = dlg.TakeReport();
        if (created == null || !ConfirmDiscard()) return;
        LoadDefinition(created, null);
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
                foreach (var f in DataLoader.GetFieldNames(_def.DataSource, new ReportStudio.Core.Data.Sources.SourceSettings { BaseDirectory = ReportFolder }))
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

        // The expression editor offers the same fields and parameters, plus the report's variables.
        ExpressionEditorContext.Fields = _fields.Items.Cast<object>().Select(o => o.ToString() ?? "")
            .Where(s => s.Length > 0 && !s.Contains('(') && s is not ("Page" or "PageCount" or "Now" or "RowNumber")).ToList();
        ExpressionEditorContext.Variables = _def.Variables.Select(v => v.Name).ToList();
        StyleEditorContext.Names = () => _def.Styles.Select(s => s.Name?.Trim() ?? "").Where(n => n.Length > 0).ToList();   // the Style list of the property grid
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
        using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.svg|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            if (ReportStudio.Core.Layout.ImageData.LooksLikeSvg(bytes) && !ReportStudio.Core.Layout.ImageData.IsSafeSvg(bytes))
            {
                MessageBox.Show(this, "This SVG drawing contains scripts or links to other files or web addresses, so it cannot be used. " +
                    "Export a plain drawing (no scripts, no external links) and try again.", "Load image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            item.ImageBase64 = Convert.ToBase64String(bytes);
            _canvas.NotifyChanged();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Load image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Puts the styles of a theme into the report: same-named styles are replaced, the others stay, elements are not touched.</summary>
    private void ApplyTheme(StyleTheme theme)
    {
        var existing = StyleThemes.StandardNames.Count(n => StyleResolver.Find(_def, n) != null);
        if (existing > 0 && MessageBox.Show(this,
                $"The {theme.Name} theme replaces {existing} style(s) of the same names in this report (Title, Heading, Body and so on). " +
                "Your own styles with other names stay. Continue?", "Apply style theme",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        int changed = StyleThemes.Apply(_def, theme);
        _canvas.NotifyChanged();
        SetDirty(true);
        _status.Text = $"{theme.Name} theme: {changed} styles added or replaced. Pick them in an element's Style property.";
    }

    /// <summary>Makes a style from the look of the selected element and gives that element the style.</summary>
    private void NewStyleFromSelection()
    {
        var item = _canvas.Selection.FirstOrDefault();
        if (item == null)
        {
            MessageBox.Show(this, "Select the element whose look you want to keep as a style, then try again.", "New style",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var ask = new NamePromptDialog("New style", "Style name:", item.Style);
        if (ask.ShowDialog(this) != DialogResult.OK) return;
        var name = ask.EnteredText().Trim();
        if (name.Length == 0) return;

        var style = StyleResolver.FromItem(StyleResolver.LookOf(_def, item), name);
        var old = StyleResolver.Find(_def, name);
        if (old != null)
        {
            if (MessageBox.Show(this, $"A style called '{old.Name}' exists already. Replace it? Elements that use it will change.", "New style",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            style.Parent = old.Parent;
            _def.Styles[_def.Styles.IndexOf(old)] = style;
        }
        else _def.Styles.Add(style);

        item.Style = style.Name;
        _canvas.NotifyChanged();
        SetDirty(true);
        OnSelectionChanged();
        _status.Text = $"Style '{style.Name}' saved and given to the element.";
    }

    private void EditSettings()
    {
        using var dlg = new ReportSettingsDialog(_def, ReportFolder);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _dbTrusted = true;   // the user just wrote or reviewed the data source themselves
        ViewerControl.ClearDataCache();   // the source may have changed: never show rows kept from the old settings
        SetLiveData(null);   // the data source may have changed: switch live data on again to reload
        _canvas.NotifyChanged();
        RefreshFields();
        SetDirty(true);
    }

    private ToolStripButton? _liveData;
    private ToolStripButton? _nextRow;

    // ---- ribbon tabs (requirement 47b): the toolbar is cut into groups at its separators and a tab shows some of the groups ----
    private ToolStrip? _bar;
    private TabControl? _ribbonTabs;
    private readonly Dictionary<ToolStripItem, int> _barGroup = new();
    private bool _nextRowWanted;
    private static readonly string[] RibbonNames = { "Home", "Insert", "Format", "View" };
    // Toolbar groups: 0 Open/Save, 1 Undo/Redo, 2 Insert tools, 3 Bold/Italic/Underline/alignment, 4 Align/Snap/Ruler/Zoom, 5 Settings/Live data/Preview
    private static readonly int[][] RibbonGroups = { new[] { 0, 1, 5 }, new[] { 2 }, new[] { 3 }, new[] { 4 } };

    private void SetRibbon(bool on)
    {
        if (_ribbonTabs == null) return;
        _ribbonTabs.Visible = on;
        DesignerUi.Settings.Ribbon = on;
        DesignerUi.Settings.Save();
        ApplyRibbon();
    }

    /// <summary>Shows the toolbar items of the selected tab (all of them when the ribbon is off).</summary>
    private void ApplyRibbon()
    {
        if (_bar == null || _ribbonTabs == null) return;
        var shown = _ribbonTabs.Visible ? RibbonGroups[Math.Clamp(_ribbonTabs.SelectedIndex, 0, RibbonGroups.Length - 1)] : null;

        foreach (var (item, group) in _barGroup)
        {
            if (item == _nextRow) continue;   // shown only while live data has more than one row
            item.Visible = shown == null || shown.Contains(group);
        }
        if (_nextRow != null && _barGroup.TryGetValue(_nextRow, out var nextGroup))
            _nextRow.Visible = _nextRowWanted && (shown == null || shown.Contains(nextGroup));
    }

    /// <summary>Shows real rows on the design canvas (or back to {Field} placeholders).</summary>
    private void ToggleLiveData()
    {
        if (_canvas.Preview != null) { SetLiveData(null); return; }

        if (_def.DataSource.Provider == DbProvider.Host)
        {
            MessageBox.Show(this, "This report takes its rows from the host application, so there is no data to show here. Use the sample data source to see values.",
                "Live data", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_def.HasOwnQuery && !_dbTrusted)
        {
            if (!UntrustedFileGuard.Confirm(this, _def, _path)) return;
            _dbTrusted = true;
            RefreshFields();
        }
        if (_def.Parameters.Count > 0 && _lastParameters.Count == 0)
        {
            using var dlg = new ParameterDialog(_def.Parameters, _lastParameters, _def);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _lastParameters = dlg.Values;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            SetLiveData(ReportStudio.Core.Layout.DesignPreview.Load(_def, _lastParameters, baseDirectory: ReportFolder));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The data could not be read:\n\n" + ex.Message, "Live data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void SetLiveData(ReportStudio.Core.Layout.DesignPreview? preview)
    {
        _canvas.Preview = preview;
        if (_nextRow == null || _liveData == null) return;   // the toolbar is not built yet
        _nextRowWanted = preview != null && preview.RowCount > 1;
        ApplyRibbon();
        _liveData.Checked = preview != null;
        UpdateLiveDataText();
    }

    private void UpdateLiveDataText()
    {
        var p = _canvas.Preview;
        if (_liveData == null) return;
        _liveData.Text = p == null ? "Live data" : $"Live data ✓ (row {p.CurrentIndex + 1} of {p.RowCount})";
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
            using var dlg = new ParameterDialog(_def.Parameters, _lastParameters, _def);
            if (dlg.ShowDialog(this) != DialogResult.OK) { _tabs.SelectedIndex = 0; return; }
            _lastParameters = dlg.Values;
        }
        _viewer.Run(_def, _lastParameters, ReportFolder);
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
