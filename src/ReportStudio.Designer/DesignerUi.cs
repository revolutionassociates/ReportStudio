using System.Runtime.CompilerServices;
using ReportStudio.Core.Ui;

namespace ReportStudio.Designer;

/// <summary>
/// The colour theme and the language of the designer's windows (requirement 47). <see cref="Apply"/> is called for the main window and
/// for every dialog when it loads: it paints the controls in the palette and swaps each English text for its translation.
/// The English text of a control is remembered, so the language can be changed while the designer runs.
/// What is not covered: message boxes and status messages written by the code stay in English, and the tab headers of a tab control
/// and the title bar of a window are drawn by Windows.
/// </summary>
internal static class DesignerUi
{
    public static DesignerSettings Settings { get; private set; } = new();
    public static UiPalette Palette { get; private set; } = UiPalette.Light;
    public static UiLanguage Language { get; private set; } = UiLanguage.English;
    public static IReadOnlyList<UiLanguage> Languages { get; private set; } = new[] { UiLanguage.English };

    private sealed class Original
    {
        public string English = "";
        public string Shown = "";
    }

    private static readonly ConditionalWeakTable<object, Original> Originals = new();
    private static readonly SortedSet<string> Seen = new(StringComparer.Ordinal);

    /// <summary>Every English text of every window that has been shown in this run, for a translator's file.</summary>
    public static IReadOnlyCollection<string> SeenTexts => Seen;

    /// <summary>Reads the settings and the language files. Call once at start-up, before any window is made.</summary>
    public static void Initialize()
    {
        Settings = DesignerSettings.Load();
        ReloadLanguages();
        Palette = Settings.Palette(WindowsIsDark());
    }

    /// <summary>Reads the language folder again and selects the language of the settings (English when it is not there any more).</summary>
    public static void ReloadLanguages()
    {
        Languages = UiLanguage.LoadFolder(DesignerSettings.LanguageFolder);
        Language = Languages.FirstOrDefault(l => string.Equals(l.Code, Settings.Language, StringComparison.OrdinalIgnoreCase)) ?? UiLanguage.English;
    }

    public static void SetTheme(string theme)
    {
        Settings.Theme = DesignerSettings.NormalizeTheme(theme);
        Settings.Save();
        Palette = Settings.Palette(WindowsIsDark());
        ReapplyAll();
    }

    public static void SetLanguage(string code)
    {
        Settings.Language = code;
        Settings.Save();
        ReloadLanguages();
        ReapplyAll();
    }

    public static void ReapplyAll()
    {
        foreach (var form in Application.OpenForms.Cast<Form>().ToList()) Apply(form);
    }

    /// <summary>True when Windows is set to dark app colours (false when that cannot be read).</summary>
    public static bool WindowsIsDark()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return value is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ================================================================ apply

    /// <summary>Put this in the <see cref="ToolStripItem.Tag"/> of an item whose text must not be translated (a language shown by its own name).</summary>
    public const string NoTranslate = "no-translate";

    /// <summary>Paints a window (or a drop-down list) in the palette and translates its texts. <paramref name="translateTitle"/> is false for a window whose title holds a file name.</summary>
    public static void Apply(Control root, bool translateTitle = true)
    {
        try
        {
            root.SuspendLayout();
            Visit(root, translateTitle);
            root.ResumeLayout(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Colours and translations are cosmetic: a control that does not take them must never stop the window from opening.
            System.Diagnostics.Debug.WriteLine("DesignerUi.Apply: " + ex.Message);
        }
    }

    private static void Visit(Control control, bool translateTitle)
    {
        Paint(control);
        Translate(control, translateTitle);

        if (control.ContextMenuStrip is { } context) VisitStrip(context);
        if (control is ToolStrip strip) VisitStrip(strip);

        // These paint their own insides (the grid's list, the spin buttons): painting the inner controls too would undo the colours set above.
        if (control is PropertyGrid or DataGridView or UpDownBase) return;
        foreach (Control child in control.Controls) Visit(child, translateTitle);
    }

    private static Color Colour(string hex) => ColorTranslator.FromHtml(hex);

    private static void Paint(Control control)
    {
        var p = Palette;
        switch (control)
        {
            case PropertyGrid grid:
                grid.BackColor = Colour(p.Back);
                grid.ViewBackColor = Colour(p.Input);
                grid.ViewForeColor = Colour(p.Fore);
                grid.ViewBorderColor = Colour(p.Border);
                grid.LineColor = Colour(p.Border);
                grid.CategoryForeColor = Colour(p.Fore);
                grid.CategorySplitterColor = Colour(p.Border);
                grid.HelpBackColor = Colour(p.Back);
                grid.HelpForeColor = Colour(p.Fore);
                grid.HelpBorderColor = Colour(p.Border);
                grid.CommandsBackColor = Colour(p.Back);
                grid.CommandsForeColor = Colour(p.Fore);
                grid.CommandsBorderColor = Colour(p.Border);
                grid.DisabledItemForeColor = Colour(p.Muted);
                break;

            case DataGridView table:
                table.EnableHeadersVisualStyles = false;
                table.BackgroundColor = Colour(p.Back);
                table.GridColor = Colour(p.Border);
                table.DefaultCellStyle.BackColor = Colour(p.Input);
                table.DefaultCellStyle.ForeColor = Colour(p.Fore);
                table.DefaultCellStyle.SelectionBackColor = Colour(p.Accent);
                table.DefaultCellStyle.SelectionForeColor = Colour(p.AccentText);
                table.ColumnHeadersDefaultCellStyle.BackColor = Colour(p.HeaderBack);
                table.ColumnHeadersDefaultCellStyle.ForeColor = Colour(p.Fore);
                table.ColumnHeadersDefaultCellStyle.SelectionBackColor = Colour(p.HeaderBack);
                table.ColumnHeadersDefaultCellStyle.SelectionForeColor = Colour(p.Fore);
                table.RowHeadersDefaultCellStyle.BackColor = Colour(p.HeaderBack);
                table.RowHeadersDefaultCellStyle.ForeColor = Colour(p.Fore);
                table.RowHeadersDefaultCellStyle.SelectionBackColor = Colour(p.HeaderBack);
                table.RowHeadersDefaultCellStyle.SelectionForeColor = Colour(p.Fore);
                break;

            case TextBoxBase or ComboBox or ListBox or ListView or TreeView or NumericUpDown:
                control.BackColor = Colour(p.Input);
                control.ForeColor = Colour(p.Fore);
                break;

            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = Colour(p.HeaderBack);
                button.ForeColor = Colour(p.Fore);
                button.FlatAppearance.BorderColor = Colour(p.Border);
                break;

            case ToolStrip:
                break;   // painted by VisitStrip

            default:
                control.BackColor = Colour(p.Back);
                control.ForeColor = Colour(p.Fore);
                break;
        }
    }

    // ---- menus and tool bars ----

    private static void VisitStrip(ToolStrip strip)
    {
        var p = Palette;
        strip.Renderer = new ToolStripProfessionalRenderer(new PaletteColours(p)) { RoundedEdges = false };
        strip.BackColor = Colour(p.Back);
        strip.ForeColor = Colour(p.Fore);
        foreach (ToolStripItem item in strip.Items) VisitItem(item);
    }

    private static void VisitItem(ToolStripItem item)
    {
        item.BackColor = Colour(Palette.Back);
        item.ForeColor = Colour(Palette.Fore);
        // A status label shows messages written by the code, and an item tagged NoTranslate shows a name (a language): neither is translated.
        if (item is not ToolStripStatusLabel && !Equals(item.Tag, NoTranslate)) Localize(item, item.Text, text => item.Text = text);

        if (item is ToolStripDropDownItem drop)
        {
            drop.DropDown.Renderer = new ToolStripProfessionalRenderer(new PaletteColours(Palette)) { RoundedEdges = false };
            drop.DropDown.BackColor = Colour(Palette.Back);
            foreach (ToolStripItem child in drop.DropDownItems) VisitItem(child);
        }
    }

    private sealed class PaletteColours : ProfessionalColorTable
    {
        private readonly Color _back, _header, _border, _hover, _fore;

        public PaletteColours(UiPalette p)
        {
            UseSystemColors = false;
            _back = Colour(p.Back);
            _header = Colour(p.HeaderBack);
            _border = Colour(p.Border);
            _hover = Colour(p.Border);
            _fore = Colour(p.Fore);
        }

        public override Color MenuStripGradientBegin => _back;
        public override Color MenuStripGradientEnd => _back;
        public override Color ToolStripGradientBegin => _header;
        public override Color ToolStripGradientMiddle => _header;
        public override Color ToolStripGradientEnd => _header;
        public override Color ToolStripBorder => _border;
        public override Color ToolStripDropDownBackground => _back;
        public override Color ToolStripContentPanelGradientBegin => _back;
        public override Color ToolStripContentPanelGradientEnd => _back;
        public override Color ToolStripPanelGradientBegin => _back;
        public override Color ToolStripPanelGradientEnd => _back;
        public override Color StatusStripGradientBegin => _header;
        public override Color StatusStripGradientEnd => _header;
        public override Color ImageMarginGradientBegin => _back;
        public override Color ImageMarginGradientMiddle => _back;
        public override Color ImageMarginGradientEnd => _back;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _border;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuItemPressedGradientBegin => _hover;
        public override Color MenuItemPressedGradientMiddle => _hover;
        public override Color MenuItemPressedGradientEnd => _hover;
        public override Color ButtonSelectedHighlight => _hover;
        public override Color ButtonSelectedGradientBegin => _hover;
        public override Color ButtonSelectedGradientMiddle => _hover;
        public override Color ButtonSelectedGradientEnd => _hover;
        public override Color ButtonSelectedBorder => _border;
        public override Color ButtonPressedHighlight => _hover;
        public override Color ButtonPressedGradientBegin => _hover;
        public override Color ButtonPressedGradientMiddle => _hover;
        public override Color ButtonPressedGradientEnd => _hover;
        public override Color ButtonPressedBorder => _border;
        public override Color ButtonCheckedHighlight => _hover;
        public override Color ButtonCheckedGradientBegin => _hover;
        public override Color ButtonCheckedGradientMiddle => _hover;
        public override Color ButtonCheckedGradientEnd => _hover;
        public override Color CheckBackground => _hover;
        public override Color CheckSelectedBackground => _hover;
        public override Color CheckPressedBackground => _hover;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _border;
        public override Color GripDark => _fore;
        public override Color GripLight => _border;
        public override Color OverflowButtonGradientBegin => _header;
        public override Color OverflowButtonGradientMiddle => _header;
        public override Color OverflowButtonGradientEnd => _header;
    }

    // ================================================================ language

    private static void Translate(Control control, bool translateTitle)
    {
        switch (control)
        {
            case Form when !translateTitle:
                break;
            case Form or TabPage or GroupBox or Label or ButtonBase:
                Localize(control, control.Text, text => control.Text = text);
                break;
            case DataGridView table:
                foreach (DataGridViewColumn column in table.Columns)
                {
                    var owner = column;
                    Localize(owner, column.HeaderText, text => owner.HeaderText = text);
                }
                break;
        }
    }

    /// <summary>
    /// Swaps the text of one control for its translation. The control's English text is remembered the first time it is seen, and found again
    /// later (when the language changes) because the control still shows what this method put there. A text the code changed in between
    /// counts as a new English text.
    /// </summary>
    private static void Localize(object owner, string? current, Action<string> set)
    {
        if (string.IsNullOrEmpty(current)) return;

        var record = Originals.GetValue(owner, _ => new Original());
        var english = record.Shown.Length > 0 && current == record.Shown ? record.English : current;
        record.English = english;
        Seen.Add(english);

        var shown = Language.Translate(english);
        record.Shown = shown;
        if (shown != current) set(shown);
    }
}
