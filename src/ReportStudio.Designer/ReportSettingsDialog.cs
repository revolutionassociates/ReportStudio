using System.Text.RegularExpressions;
using ReportStudio.Core.Data;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;

namespace ReportStudio.Designer;

/// <summary>Edits everything about a report that is not on the canvas: data source, parameters, groups, sort and page.</summary>
public sealed class ReportSettingsDialog : Form
{
    private readonly ReportDefinition _def;

    // data source
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox _connection = new() { Dock = DockStyle.Fill };
    private readonly TextBox _query = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f)
    };
    private readonly NumericUpDown _timeout = new() { Minimum = 5, Maximum = 3600, Value = 60, Width = 80 };
    private readonly TextBox _fields = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f)
    };

    // data source: files and web addresses (requirement 40)
    private readonly string? _baseDirectory;      // the report file's folder: a file named without a folder is looked up here
    private readonly TextBox _source = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = "Browse...", AutoSize = true };
    private readonly TextBox _selector = new() { Dock = DockStyle.Fill };
    private readonly TextBox _delimiter = new() { Width = 60 };
    private readonly CheckBox _hasHeader = new() { Text = "The first row holds the field names", AutoSize = true, Checked = true };
    private readonly TextBox _headers = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 56, Font = new Font("Consolas", 10f)
    };
    private readonly Label _queryHint = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 4), MaximumSize = new Size(690, 0) };
    private Label _sourceLabel = new(), _selectorLabel = new();
    // The controls of each kind of source: shown only for the provider they belong to.
    private readonly List<Control> _rowsDatabase = new(), _rowsSource = new(), _rowsSelector = new(), _rowsCsv = new(), _rowsHeader = new(), _rowsWeb = new(), _rowsCache = new();
    private readonly NumericUpDown _cacheSeconds = new() { Minimum = 0, Maximum = DataCache.MaxSeconds, Width = 80 };

    // others
    private readonly DataGridView _params = NewGrid();
    private readonly DataGridView _groups = NewGrid();
    private readonly DataGridView _dataSets = NewGrid();
    private readonly DataGridView _joins = NewGrid();
    private readonly TextBox _sort = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly DataGridView _vars = NewGrid();
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, Font = new Font("Consolas", 10f) };
    private readonly Label _filterStatus = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly TextBox _name = new() { Width = 300 };
    private readonly ComboBox _pageSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly NumericUpDown _mL = NewMargin(), _mR = NewMargin(), _mT = NewMargin(), _mB = NewMargin();

    // columns and label sheets
    private readonly NumericUpDown _columns = new() { Minimum = 1, Maximum = PageSettings.MaxColumns, Width = 80 };
    private readonly NumericUpDown _columnGap = new() { Minimum = 0, Maximum = 400, DecimalPlaces = 1, Width = 80 };
    private readonly ComboBox _columnOrder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _labelSheet = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly Label _columnInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(480, 0) };
    private LabelPreset? _pendingLabelSheet;     // chosen in this dialog, applied to the Details band and the empty bands on OK

    // page look: background
    private readonly TextBox _bgColor = new() { Width = 100 };
    private readonly TextBox _bgGradient = new() { Width = 100 };
    private readonly ComboBox _bgDirection = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };

    // page look: watermark
    private readonly TextBox _wmText = new() { Width = 320 };
    private readonly TextBox _wmColor = new() { Width = 100 };
    private readonly NumericUpDown _wmOpacity = new() { Minimum = 0, Maximum = 100, Width = 80 };
    private readonly NumericUpDown _wmAngle = new() { Minimum = -360, Maximum = 360, Width = 80 };
    private readonly NumericUpDown _wmSize = new() { Minimum = 6, Maximum = 600, Width = 80 };
    private readonly CheckBox _wmBold = new() { Text = "Bold", AutoSize = true };
    private string _wmImage = "";                       // base64 of the chosen watermark picture; "" = none
    private readonly Label _wmPictureName = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0), Text = "(none)" };
    private readonly NumericUpDown _wmPictureSize = new() { Minimum = 5, Maximum = 100, Width = 80 };

    private const string SameAsReport = "(report / outer group)";
    private const string CustomPage = "(custom size, kept)";

    private static string GroupPageLabel(GroupDefinition g)
    {
        if (g.PageWidth <= 0 || g.PageHeight <= 0) return SameAsReport;
        int at = Array.FindIndex(PageSizes, p => Math.Abs(p.W - g.PageWidth) < 2 && Math.Abs(p.H - g.PageHeight) < 2);
        return at >= 0 ? PageSizes[at].Label : CustomPage;
    }

    private static readonly (string Label, double W, double H)[] PageSizes =
    {
        ("A4 portrait", 794, 1123), ("A4 landscape", 1123, 794),
        ("Letter portrait", 816, 1056), ("Letter landscape", 1056, 816),
        ("A5 portrait", 559, 794), ("A5 landscape", 794, 559)
    };

    public ReportSettingsDialog(ReportDefinition def, string? baseDirectory = null)
    {
        _def = def;
        _baseDirectory = baseDirectory;
        Text = "Report settings";
        Size = new Size(760, 600);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildPageLookTab());
        tabs.TabPages.Add(BuildDataTab());
        tabs.TabPages.Add(BuildParametersTab());
        tabs.TabPages.Add(BuildGroupsTab());
        tabs.TabPages.Add(BuildDataSetsTab());
        tabs.TabPages.Add(BuildVariablesTab());

        var ok = new Button { Text = "OK", Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => { if (Apply()) DialogResult = DialogResult.OK; };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        LoadValues();
    }

    // ---- tabs ----------------------------------------------------------------------------------

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General") { Padding = new Padding(12) };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _pageSize.Items.AddRange(PageSizes.Select(p => (object)p.Label).ToArray());

        AddRow(t, "Report name", _name);
        AddRow(t, "Page size", _pageSize);
        AddRow(t, "Margin left (px)", _mL);
        AddRow(t, "Margin right (px)", _mR);
        AddRow(t, "Margin top (px)", _mT);
        AddRow(t, "Margin bottom (px)", _mB);

        _columnOrder.Items.AddRange(new object[] { "Down - fill a column, then the next (lists)", "Across - left to right, then the next line (labels)" });
        _labelSheet.Items.Add("(none)");
        _labelSheet.Items.AddRange(LabelPreset.All.Select(p => (object)p.Name).ToArray());
        _labelSheet.SelectedIndex = 0;
        AddRow(t, "Columns", _columns);
        AddRow(t, "Column gap (px)", _columnGap);
        AddRow(t, "Column order", _columnOrder);
        AddRow(t, "Label sheet", _labelSheet);
        AddRow(t, "", _columnInfo);

        foreach (var n in new[] { _mL, _mR, _columns, _columnGap }) n.ValueChanged += (_, _) => UpdateColumnInfo();
        _pageSize.SelectedIndexChanged += (_, _) => UpdateColumnInfo();
        _labelSheet.SelectedIndexChanged += (_, _) => ChooseLabelSheet();

        page.Controls.Add(t);
        return page;
    }

    private TabPage BuildPageLookTab()
    {
        var page = new TabPage("Page look") { Padding = new Padding(12) };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _bgDirection.Items.AddRange(new object[] { "Top to bottom", "Left to right" });
        AddRow(t, "Page colour (hex)", _bgColor);
        AddRow(t, "Gradient colour (hex)", _bgGradient);
        AddRow(t, "Gradient direction", _bgDirection);
        AddRow(t, "", new Label { Text = "", AutoSize = true, Height = 12 });
        AddRow(t, "Watermark text", _wmText);
        AddRow(t, "Colour (hex)", _wmColor);
        AddRow(t, "Opacity (%)", _wmOpacity);
        AddRow(t, "Angle (degrees)", _wmAngle);
        AddRow(t, "Font size (pt)", _wmSize);
        AddRow(t, "", _wmBold);

        var choose = new Button { Text = "Choose...", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        choose.Click += (_, _) => ChooseWatermarkPicture();
        remove.Click += (_, _) => { _wmImage = ""; ShowWatermarkPicture(); };
        var pictureRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        pictureRow.Controls.AddRange(new Control[] { choose, remove, _wmPictureName });
        AddRow(t, "Watermark picture", pictureRow);
        AddRow(t, "Picture width (% of page)", _wmPictureSize);

        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 10, 0, 0), MaximumSize = new Size(680, 0),
            Text = "The page colour fills the whole page behind everything else; add a gradient colour to fade from the page colour to it. " +
                   "Leave the page colour empty for a plain page. " +
                   "The watermark is faint text behind the content of every page, such as DRAFT or CONFIDENTIAL. " +
                   "It may use {expressions}, e.g. {@Status}. Leave the text empty for no watermark. " +
                   "Angle 0 runs straight along the page, 45 rises to the right. A long text is made smaller to fit the page. " +
                   "A watermark picture (PNG, JPEG, GIF, BMP, TIFF or a plain SVG drawing) is printed the same way, with the same opacity and angle, " +
                   "with or without text; it is made smaller when, turned, it would not fit on the page."
        };
        page.Controls.Add(hint);
        page.Controls.Add(t);
        return page;
    }

    private TabPage BuildDataTab()
    {
        var page = new TabPage("Data source") { Padding = new Padding(12) };
        _provider.Items.AddRange(new object[]
        {
            "Sample data (no database)", "SQL Server", "MySQL / MariaDB", "PostgreSQL",
            "Host application (rows supplied by the host)",
            "JSON file", "XML file", "CSV / text file", "Excel workbook (.xlsx)", "REST web address (JSON)", "OData web address"
        });      // in the order of DbProvider, so the position is the value
        _provider.SelectedIndexChanged += (_, _) => UpdateProviderState();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(top, "Provider", _provider);
        AddRow(top, "Connection string", _connection, _rowsDatabase);
        AddRow(top, "Command timeout (s)", _timeout, _rowsDatabase);

        var sourceCell = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        sourceCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceCell.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceCell.Controls.Add(_source);
        sourceCell.Controls.Add(_browse);
        _browse.Click += (_, _) => BrowseForSource();
        _sourceLabel = AddRow(top, "File", sourceCell, _rowsSource);
        _selectorLabel = AddRow(top, "Rows are at", _selector, _rowsSelector);
        AddRow(top, "Separator", _delimiter, _rowsCsv);
        AddRow(top, "", _hasHeader, _rowsHeader);
        AddRow(top, "Request headers", _headers, _rowsWeb);
        AddRow(top, "Reuse rows for (s)", _cacheSeconds, _rowsCache);      // 0 = read the source again on every run

        var test = new Button { Text = "Test && load fields", AutoSize = true };
        test.Click += (_, _) => TestConnection();

        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_query);
        body.Controls.Add(_queryHint);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        bottom.Controls.Add(test);

        var fieldsPanel = new Panel { Dock = DockStyle.Bottom, Height = 130, Padding = new Padding(0, 6, 0, 0) };
        fieldsPanel.Controls.Add(_fields);
        fieldsPanel.Controls.Add(new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4),
            Text = "Fields the report uses, one per line as Name:Type (Text, Number, Date, Boolean) — fills the field list without a database:"
        });

        page.Controls.Add(body);
        page.Controls.Add(fieldsPanel);
        page.Controls.Add(bottom);
        page.Controls.Add(top);
        return page;
    }

    private TabPage BuildParametersTab()
    {
        var page = new TabPage("Parameters") { Padding = new Padding(8) };
        _params.Columns.Add("Name", "Name");
        _params.Columns.Add("Prompt", "Prompt shown to the user");
        _params.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Type", HeaderText = "Type", ValueType = typeof(ParameterType),
            DataSource = Enum.GetValues<ParameterType>(), FlatStyle = FlatStyle.Flat
        });
        _params.Columns.Add("Default", "Default value (start with = for an expression, e.g. =MonthStart(Today()))");
        _params.Columns.Add("Choices", "Drop-down list: A;B;C or Code=Text;...");
        _params.Columns.Add("ChoicesQuery", "...or read from the database (value, text)");
        _params.Columns.Add("DependsOn", "List follows parameters (names, ;)");
        _params.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Multi", HeaderText = "Pick several" });
        page.Controls.Add(_params);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6),
            Text = "Use a parameter in the SQL as @Name and on the report as {@Name}. Dates are yyyy-MM-dd. " +
                   "\"Pick several\" gives a list: SQL  WHERE Dept IN (@Name)  or an expression  In(Dept, @Name)."
        });
        return page;
    }

    /// <summary>Other data sets of the report (read with Lookup... functions) and the joins that add their fields to the main rows (requirement 39).</summary>
    private TabPage BuildDataSetsTab()
    {
        var page = new TabPage("Data sets && joins") { Padding = new Padding(8) };

        _dataSets.Columns.Add("SetName", "Name (used in Lookup and joins)");
        var provider = new DataGridViewComboBoxColumn { Name = "SetProvider", HeaderText = "Source", FlatStyle = FlatStyle.Flat };
        provider.Items.AddRange(Enum.GetNames<DbProvider>().Cast<object>().ToArray());
        _dataSets.Columns.Add(provider);
        _dataSets.Columns.Add("SetConnection", "Connection string (database sources)");
        _dataSets.Columns.Add("SetQuery", "Query (database sources; @Parameter works)");
        _dataSets.Columns.Add("SetSource", "File or web address (Json, Xml, Csv, Excel, Rest, OData)");
        _dataSets.Columns.Add("SetSelector", "Rows at / sheet name");
        _dataSets.Columns.Add("SetHeaders", "Web headers (Name: value | Name: value)");
        _dataSets.Columns.Add("SetCache", "Reuse rows for (s, 0 = no)");

        var setBox = new GroupBox
        {
            Text = "Data sets. Host = the application supplies the rows under this name. Sample = the built-in sample rows.",
            Dock = DockStyle.Top, Height = 200
        };
        setBox.Controls.Add(_dataSets);

        _joins.Columns.Add("JoinSet", "Join data set");
        _joins.Columns.Add("JoinLeft", "Field of the report's rows");
        _joins.Columns.Add("JoinRight", "Field of the data set (empty = same)");
        var kind = new DataGridViewComboBoxColumn { Name = "JoinKind", HeaderText = "Kind", FlatStyle = FlatStyle.Flat };
        kind.Items.AddRange(Enum.GetNames<JoinKind>().Cast<object>().ToArray());
        _joins.Columns.Add(kind);
        _joins.Columns.Add("JoinPrefix", "Prefix for its fields (e.g. Dept_)");

        var joinBox = new GroupBox
        {
            Text = "Joins, applied in this order before the report filters, sorts and groups. Left keeps every row; Inner drops rows with no match; several matches give several rows.",
            Dock = DockStyle.Fill
        };
        joinBox.Controls.Add(_joins);

        page.Controls.Add(joinBox);
        page.Controls.Add(setBox);
        return page;
    }

    private TabPage BuildGroupsTab()
    {
        var page = new TabPage("Grouping && sort") { Padding = new Padding(8) };
        _groups.Columns.Add("Expression", "Group by (field or expression)");
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "NewPage", HeaderText = "Start each group on a new page" });
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "RestartPages", HeaderText = "Restart page numbers" });
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Collapsible", HeaderText = "Drill-down: click header to open/close" });
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "StartCollapsed", HeaderText = "Start closed" });
        var pageColumn = new DataGridViewComboBoxColumn { Name = "GroupPage", HeaderText = "Page size of its pages (needs a new page)", FlatStyle = FlatStyle.Flat };
        pageColumn.Items.Add(SameAsReport);
        pageColumn.Items.AddRange(PageSizes.Select(p => (object)p.Label).ToArray());
        pageColumn.Items.Add(CustomPage);
        _groups.Columns.Add(pageColumn);
        _groups.Columns.Add("PageWhen", "Page size only when (expression, empty = always)");

        var groupBox = new GroupBox
        {
            Text = "Groups (outermost first). Each group has its own header and footer band.",
            Dock = DockStyle.Top, Height = 230
        };
        groupBox.Controls.Add(_groups);

        var sortBox = new GroupBox { Text = "Extra sort expressions (one per line, applied inside the groups)", Dock = DockStyle.Fill };
        sortBox.Controls.Add(_sort);

        page.Controls.Add(sortBox);
        page.Controls.Add(groupBox);
        return page;
    }

    private TabPage BuildVariablesTab()
    {
        var page = new TabPage("Variables && filter") { Padding = new Padding(8) };

        _vars.Columns.Add("Name", "Name");
        _vars.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Function", HeaderText = "What it does", ValueType = typeof(VariableFunction),
            DataSource = Enum.GetValues<VariableFunction>(), FlatStyle = FlatStyle.Flat
        });
        _vars.Columns.Add("Expression", "Value of each row");
        _vars.Columns.Add("Condition", "Only rows where (optional)");
        _vars.Columns.Add("Initial", "Start value (optional)");
        _vars.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Reset", HeaderText = "Start again on", ValueType = typeof(VariableReset),
            DataSource = Enum.GetValues<VariableReset>(), FlatStyle = FlatStyle.Flat
        });
        _vars.Columns.Add("ResetGroup", "Group level");

        var varBox = new GroupBox
        {
            Text = "Variables: running totals and shared values, shown anywhere as {Name}",
            Dock = DockStyle.Fill, Padding = new Padding(6)
        };
        varBox.Controls.Add(_vars);
        varBox.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0),
            Text = "Examples: RunningTotal = Sum of Amount.  Balance = Assign \"Balance + Credit - Debit\".  " +
                   "Group level 1 is the outermost group; it only matters when 'Start again on' is Group."
        });

        var filterBox = new GroupBox
        {
            Text = "Record filter: only rows where this is true are used (empty = all rows)",
            Dock = DockStyle.Top, Height = 92, Padding = new Padding(8, 6, 8, 6)
        };
        var filterRow = new Panel { Dock = DockStyle.Top, Height = 26 };
        _filter.Dock = DockStyle.Fill;
        var filterFx = new Button { Text = "ƒx…", Dock = DockStyle.Right, Width = 64 };
        filterFx.Click += (_, _) => { if (EditExpression("Record filter", _filter.Text) is { } edited) _filter.Text = edited; };
        filterRow.Controls.Add(_filter);
        filterRow.Controls.Add(filterFx);
        filterBox.Controls.Add(_filterStatus);
        filterBox.Controls.Add(filterRow);
        _filter.TextChanged += (_, _) => ShowFilterStatus();

        var cellFx = new Button { Text = "Edit the selected value / condition / start value with the expression editor…", AutoSize = true };
        cellFx.Click += (_, _) => EditSelectedVariableCell();
        var cellBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        cellBar.Controls.Add(cellFx);
        varBox.Controls.Add(cellBar);

        page.Controls.Add(varBox);
        page.Controls.Add(filterBox);
        return page;
    }

    /// <summary>Opens the expression editor; null when the user cancelled.</summary>
    private string? EditExpression(string title, string current)
    {
        using var dlg = new ExpressionEditorDialog(title, current);
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.Expression : null;
    }

    private void EditSelectedVariableCell()
    {
        var cell = _vars.CurrentCell;
        if (cell == null || cell.OwningRow == null || cell.OwningRow.IsNewRow || cell.ColumnIndex < 2 || cell.ColumnIndex > 4)
        {
            MessageBox.Show(this, "Click first on a Value, Condition or Start value cell of a variable.", Text);
            return;
        }
        if (EditExpression(_vars.Columns[cell.ColumnIndex].HeaderText, Convert.ToString(cell.Value) ?? "") is { } edited)
            cell.Value = edited;
    }

    private void ShowFilterStatus()
    {
        var problem = DefinitionChecks.CheckExpression(_filter.Text);
        _filterStatus.ForeColor = problem == null ? SystemColors.GrayText : Color.Firebrick;
        _filterStatus.Text = problem != null ? "Not valid: " + problem
            : string.IsNullOrWhiteSpace(_filter.Text) ? "Example: Status == 'Active' && Net > 0" : "The expression is valid.";
    }

    /// <summary>The variables typed in the grid (rows without a name are ignored, like parameters).</summary>
    private List<VariableDefinition> ReadVariables()
    {
        var list = new List<VariableDefinition>();
        foreach (DataGridViewRow row in _vars.Rows)
        {
            if (row.IsNewRow) continue;
            string Cell(int cell) => (Convert.ToString(row.Cells[cell].Value) ?? "").Trim();
            if (Cell(0).Length == 0) continue;

            list.Add(new VariableDefinition
            {
                Name = Cell(0),
                Function = row.Cells[1].Value is VariableFunction f ? f : VariableFunction.Sum,
                Expression = Cell(2),
                Condition = Cell(3),
                Initial = Cell(4),
                Reset = row.Cells[5].Value is VariableReset r ? r : VariableReset.Never,
                ResetGroup = int.TryParse(Cell(6), out var level) ? level : 1
            });
        }
        return list;
    }

    // ---- load / apply --------------------------------------------------------------------------

    private void LoadValues()
    {
        _name.Text = _def.Name;
        int sizeIndex = Array.FindIndex(PageSizes,
            p => Math.Abs(p.W - _def.Page.Width) < 2 && Math.Abs(p.H - _def.Page.Height) < 2);
        _pageSize.SelectedIndex = sizeIndex >= 0 ? sizeIndex : 0;
        _mL.Value = Clamp(_def.Page.MarginLeft);
        _mR.Value = Clamp(_def.Page.MarginRight);
        _mT.Value = Clamp(_def.Page.MarginTop);
        _mB.Value = Clamp(_def.Page.MarginBottom);
        _columns.Value = Math.Clamp(_def.Page.ColumnCount, 1, PageSettings.MaxColumns);
        _columnGap.Value = Clamp(_def.Page.ColumnGap);
        _columnOrder.SelectedIndex = _def.Page.ColumnOrder == ColumnOrder.Across ? 1 : 0;
        UpdateColumnInfo();

        _bgColor.Text = _def.Background.Color;
        _bgGradient.Text = _def.Background.GradientColor;
        _bgDirection.SelectedIndex = _def.Background.Direction == GradientDirection.LeftToRight ? 1 : 0;

        var wm = _def.Watermark;
        _wmText.Text = wm.Text;
        _wmColor.Text = wm.Color;
        _wmOpacity.Value = (decimal)Math.Clamp(Math.Round(wm.Opacity * 100), 0, 100);
        _wmAngle.Value = (decimal)Math.Clamp(Math.Round(wm.Angle), -360, 360);
        _wmSize.Value = (decimal)Math.Clamp(Math.Round(wm.FontSize), 6, 600);
        _wmBold.Checked = wm.Bold;
        _wmImage = wm.ImageBase64 ?? "";
        _wmPictureSize.Value = (decimal)Math.Clamp(Math.Round(wm.ImageSize), 5, 100);
        ShowWatermarkPicture();

        _provider.SelectedIndex = (int)_def.DataSource.Provider;
        UpdateProviderState();
        _connection.Text = _def.DataSource.ConnectionString;
        _query.Text = _def.DataSource.Query.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _timeout.Value = Math.Clamp(_def.DataSource.CommandTimeoutSeconds, 5, 3600);
        _fields.Text = string.Join(Environment.NewLine, _def.DataSource.Fields.Select(f => f.ToString()));
        _source.Text = _def.DataSource.Source;
        _selector.Text = _def.DataSource.Selector;
        _delimiter.Text = _def.DataSource.Delimiter;
        _hasHeader.Checked = _def.DataSource.HasHeaderRow;
        _headers.Text = _def.DataSource.Headers.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _cacheSeconds.Value = Math.Clamp(_def.DataSource.CacheSeconds, 0, DataCache.MaxSeconds);

        foreach (var p in _def.Parameters) _params.Rows.Add(p.Name, p.Prompt, p.Type, p.DefaultValue, p.Choices, p.ChoicesQuery, p.DependsOn, p.MultiValue);
        foreach (var d in _def.DataSets)
        {
            var at = _dataSets.Rows.Add(d.Name, d.DataSource.Provider.ToString(), d.DataSource.ConnectionString, d.DataSource.Query,
                d.DataSource.Source, d.DataSource.Selector, HeadersToCell(d.DataSource.Headers), d.DataSource.CacheSeconds);
            _dataSets.Rows[at].Tag = d.DataSource;      // the separator and header-row settings have no column: kept as they are
        }
        foreach (var j in _def.Joins)
            _joins.Rows.Add(j.DataSet, j.LeftField, j.RightField, j.Kind.ToString(), j.Prefix);
        foreach (var g in _def.Groups) _groups.Rows.Add(g.Expression, g.StartNewPage, g.RestartPageNumbers, g.Collapsible, g.StartCollapsed, GroupPageLabel(g), g.PageSizeWhen);
        _sort.Text = string.Join(Environment.NewLine, _def.SortBy);

        _filter.Text = _def.Filter;
        ShowFilterStatus();
        foreach (var v in _def.Variables)
            _vars.Rows.Add(v.Name, v.Function, v.Expression, v.Condition, v.Initial, v.Reset, v.ResetGroup);
    }

    /// <summary>Tells how wide one column is, since the Details band has to be designed for that width.</summary>
    private void UpdateColumnInfo()
    {
        var size = PageSizes[Math.Max(0, _pageSize.SelectedIndex)];
        int columns = (int)_columns.Value;
        double content = size.W - (double)_mL.Value - (double)_mR.Value;
        double width = Math.Max(1, (content - (double)_columnGap.Value * (columns - 1)) / columns);
        _columnInfo.Text = columns < 2
            ? "One column: the Details band uses the whole width between the margins."
            : $"Each column is {width:0.#} px wide. Design the Details band for that width; group headers and footers keep the full width.";
    }

    /// <summary>A label sheet fills in the page, margins and columns; the Details band is set to one label high when OK is pressed.</summary>
    private void ChooseLabelSheet()
    {
        if (_labelSheet.SelectedIndex <= 0) { _pendingLabelSheet = null; return; }
        var preset = LabelPreset.All[_labelSheet.SelectedIndex - 1];
        var scratch = new ReportDefinition();
        preset.ApplyTo(scratch);

        int sizeIndex = Array.FindIndex(PageSizes, p => Math.Abs(p.W - scratch.Page.Width) < 2 && Math.Abs(p.H - scratch.Page.Height) < 2);
        if (sizeIndex >= 0) _pageSize.SelectedIndex = sizeIndex;
        _mL.Value = Clamp(scratch.Page.MarginLeft);
        _mR.Value = Clamp(scratch.Page.MarginRight);
        _mT.Value = Clamp(scratch.Page.MarginTop);
        _mB.Value = Clamp(scratch.Page.MarginBottom);
        _columns.Value = scratch.Page.ColumnCount;
        _columnGap.Value = Clamp(scratch.Page.ColumnGap);
        _columnOrder.SelectedIndex = 1;
        _pendingLabelSheet = preset;
        UpdateColumnInfo();
    }

    private static decimal Clamp(double v) => (decimal)Math.Clamp(v, 0, 400);

    private bool Apply()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newParams = new List<ReportParameter>();
        foreach (DataGridViewRow row in _params.Rows)
        {
            if (row.IsNewRow) continue;
            var name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "";
            if (name.Length == 0) continue;
            if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                MessageBox.Show(this, $"'{name}' is not a valid parameter name (letters, digits and _ only).", Text);
                return false;
            }
            if (!names.Add(name))
            {
                MessageBox.Show(this, $"Parameter '{name}' is listed twice.", Text);
                return false;
            }
            newParams.Add(new ReportParameter
            {
                Name = name,
                Prompt = Convert.ToString(row.Cells[1].Value) ?? "",
                Type = row.Cells[2].Value is ParameterType t ? t : ParameterType.Text,
                DefaultValue = Convert.ToString(row.Cells[3].Value) ?? "",
                Choices = (Convert.ToString(row.Cells[4].Value) ?? "").Trim(),
                ChoicesQuery = (Convert.ToString(row.Cells[5].Value) ?? "").Trim(),
                DependsOn = (Convert.ToString(row.Cells[6].Value) ?? "").Trim(),
                MultiValue = row.Cells[7].Value is true
            });
        }

        // Variables and the filter are checked before anything is stored, so a mistake changes nothing.
        var newVariables = ReadVariables();
        int groupCount = _groups.Rows.Cast<DataGridViewRow>()
            .Count(r => !r.IsNewRow && !string.IsNullOrWhiteSpace(Convert.ToString(r.Cells[0].Value)));
        var problems = DefinitionChecks.CheckVariables(newVariables, groupCount);
        var filterProblem = DefinitionChecks.CheckExpression(_filter.Text);
        if (filterProblem != null) problems.Insert(0, "The record filter is not valid (" + filterProblem + ")");
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(8)), "Variables && filter",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // The page colours are checked here too, before anything is stored.
        foreach (var (box, what) in new[] { (_bgColor, "page"), (_bgGradient, "gradient") })
            if (box.Text.Trim().Length > 0 && ColorUtil.Parse(box.Text) == null)
            {
                MessageBox.Show(this, $"The {what} colour is not valid. Write it as #RRGGBB, for example #FFF8E1.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

        _def.Background.Color = _bgColor.Text.Trim();
        _def.Background.GradientColor = _bgGradient.Text.Trim();
        _def.Background.Direction = _bgDirection.SelectedIndex == 1 ? GradientDirection.LeftToRight : GradientDirection.TopToBottom;

        // The watermark colour is checked here too, before anything is stored.
        if (_wmText.Text.Trim().Length > 0 && ColorUtil.Parse(_wmColor.Text) == null)
        {
            MessageBox.Show(this, "The watermark colour is not valid. Write it as #RRGGBB, for example #9CA3AF.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _def.Watermark.Text = _wmText.Text.Trim();
        _def.Watermark.Color = _wmColor.Text.Trim();
        _def.Watermark.Opacity = (double)_wmOpacity.Value / 100;
        _def.Watermark.Angle = (double)_wmAngle.Value;
        _def.Watermark.FontSize = (double)_wmSize.Value;
        _def.Watermark.Bold = _wmBold.Checked;
        _def.Watermark.ImageBase64 = _wmImage;
        _def.Watermark.ImageSize = (double)_wmPictureSize.Value;

        _def.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled Report" : _name.Text.Trim();
        _def.Filter = _filter.Text.Trim();
        _def.Variables = newVariables;
        var size = PageSizes[Math.Max(0, _pageSize.SelectedIndex)];
        _def.Page.Width = size.W;
        _def.Page.Height = size.H;
        _def.Page.MarginLeft = (double)_mL.Value;
        _def.Page.MarginRight = (double)_mR.Value;
        _def.Page.MarginTop = (double)_mT.Value;
        _def.Page.MarginBottom = (double)_mB.Value;
        _def.Page.Columns = (int)_columns.Value;
        _def.Page.ColumnGap = (double)_columnGap.Value;
        _def.Page.ColumnOrder = _columnOrder.SelectedIndex == 1 ? ColumnOrder.Across : ColumnOrder.Down;
        if (_pendingLabelSheet != null)
        {
            // The page values above are the sheet's own; the rows of a label are one label high and a sheet has no header or footer unless one has elements.
            _def.Detail.Height = LabelPreset.ToPixels(_pendingLabelSheet.LabelHeightMm);
            foreach (var band in new[] { _def.ReportHeader, _def.PageHeader, _def.PageFooter, _def.ReportFooter })
                if (band.Items.Count == 0) band.Height = 0;
        }

        var mainProvider = (DbProvider)Math.Max(0, _provider.SelectedIndex);
        _def.DataSource.Provider = mainProvider;
        bool host = mainProvider == DbProvider.Host;
        bool mainFile = mainProvider.IsFile(), mainWeb = mainProvider.IsWeb();
        // A Host report never stores a query or connection string, and a file or web source has none either.
        _def.DataSource.ConnectionString = host || mainFile || mainWeb ? "" : _connection.Text.Trim();
        _def.DataSource.Query = host || mainFile || mainWeb ? "" : _query.Text;
        _def.DataSource.Source = mainFile || mainWeb ? _source.Text.Trim() : "";
        _def.DataSource.Selector = (mainFile || mainWeb) && mainProvider != DbProvider.Csv ? _selector.Text.Trim() : "";
        _def.DataSource.Delimiter = mainProvider == DbProvider.Csv ? _delimiter.Text : "";
        _def.DataSource.HasHeaderRow = (mainProvider is DbProvider.Csv or DbProvider.Excel) ? _hasHeader.Checked : true;
        _def.DataSource.Headers = mainWeb ? CellToHeaders(_headers.Text) : "";
        _def.DataSource.CacheSeconds = (mainProvider is DbProvider.Sample or DbProvider.Host) ? 0 : (int)_cacheSeconds.Value;
        _def.DataSource.CommandTimeoutSeconds = (int)_timeout.Value;
        _def.DataSource.Fields = FieldDefinition.ParseLines(_fields.Lines);
        _def.Parameters = newParams;

        // Groups keep their header/footer bands by position; extra rows add groups, missing rows remove them.
        var rows = _groups.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && !string.IsNullOrWhiteSpace(Convert.ToString(r.Cells[0].Value))).ToList();
        var groups = new List<GroupDefinition>();
        for (int i = 0; i < rows.Count; i++)
        {
            var g = i < _def.Groups.Count ? _def.Groups[i] : new GroupDefinition();
            g.Expression = Convert.ToString(rows[i].Cells[0].Value)!.Trim();
            g.StartNewPage = rows[i].Cells[1].Value is true;
            g.RestartPageNumbers = rows[i].Cells[2].Value is true;
            g.Collapsible = rows[i].Cells[3].Value is true;
            g.StartCollapsed = rows[i].Cells[4].Value is true;
            var chosen = Convert.ToString(rows[i].Cells[5].Value) ?? SameAsReport;
            var size = Array.FindIndex(PageSizes, p => p.Label == chosen);
            if (size >= 0) { g.PageWidth = PageSizes[size].W; g.PageHeight = PageSizes[size].H; }
            else if (chosen != CustomPage) { g.PageWidth = 0; g.PageHeight = 0; }      // custom: keep what the file has
            g.PageSizeWhen = (Convert.ToString(rows[i].Cells[6].Value) ?? "").Trim();
            groups.Add(g);
        }
        _def.Groups = groups;

        string Text(DataGridViewRow row, int cell) => (Convert.ToString(row.Cells[cell].Value) ?? "").Trim();
        _def.DataSets = _dataSets.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && Text(r, 0).Length > 0)
            .Select(r =>
            {
                var dsProvider = Enum.TryParse<DbProvider>(Text(r, 1), out var parsedProvider) ? parsedProvider : DbProvider.Host;
                var dsKept = r.Tag as DataSourceDefinition;
                bool dsFile = dsProvider.IsFile(), dsWeb = dsProvider.IsWeb(), dsOutside = dsFile || dsWeb;
                return new DataSetDefinition
                {
                    Name = Text(r, 0),
                    DataSource = new DataSourceDefinition
                    {
                        Provider = dsProvider,
                        ConnectionString = dsOutside ? "" : Text(r, 2),
                        Query = dsOutside ? "" : Text(r, 3),
                        Source = dsOutside ? Text(r, 4) : "",
                        Selector = dsOutside && dsProvider != DbProvider.Csv ? Text(r, 5) : "",
                        Headers = dsWeb ? CellToHeaders(Text(r, 6)) : "",
                        Delimiter = dsFile && dsProvider == DbProvider.Csv ? dsKept?.Delimiter ?? "" : "",
                        HasHeaderRow = dsKept?.HasHeaderRow ?? true,
                        CacheSeconds = (dsProvider is DbProvider.Sample or DbProvider.Host)
                            ? 0 : int.TryParse(Text(r, 7), out var dsSeconds) ? Math.Clamp(dsSeconds, 0, DataCache.MaxSeconds) : 0
                    }
                };
            }).ToList();
        _def.Joins = _joins.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && Text(r, 0).Length > 0)
            .Select(r => new JoinDefinition
            {
                DataSet = Text(r, 0), LeftField = Text(r, 1), RightField = Text(r, 2),
                Kind = Enum.TryParse<JoinKind>(Text(r, 3), out var parsedKind) ? parsedKind : JoinKind.Left, Prefix = Text(r, 4)
            }).ToList();
        _def.SortBy = _sort.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _def.Normalize();
        return true;
    }

    /// <summary>Shows only the settings that belong to the chosen provider: a database, a file, or a web address.</summary>
    private void UpdateProviderState()
    {
        var provider = (DbProvider)Math.Max(0, _provider.SelectedIndex);
        bool host = provider == DbProvider.Host, outside = provider.IsFile() || provider.IsWeb();

        _connection.Enabled = _query.Enabled = _timeout.Enabled = !host;
        if (host || outside) { _connection.Text = ""; _query.Text = ""; }

        foreach (var c in _rowsDatabase) c.Visible = !outside;
        foreach (var c in _rowsSource) c.Visible = outside;
        foreach (var c in _rowsSelector) c.Visible = outside && provider != DbProvider.Csv;
        foreach (var c in _rowsCsv) c.Visible = provider == DbProvider.Csv;
        foreach (var c in _rowsHeader) c.Visible = provider is DbProvider.Csv or DbProvider.Excel;
        foreach (var c in _rowsWeb) c.Visible = provider.IsWeb();
        foreach (var c in _rowsCache) c.Visible = provider is not (DbProvider.Sample or DbProvider.Host);
        _browse.Visible = provider.IsFile();
        _query.Visible = !outside;

        _sourceLabel.Text = provider.IsWeb() ? "Web address" : "File";
        _selectorLabel.Text = provider switch
        {
            DbProvider.Json => "Rows are at (path, e.g. data.items; empty = whole file)",
            DbProvider.Xml => "Rows are the elements (e.g. orders/order)",
            DbProvider.Excel => "Sheet name (empty = first sheet)",
            DbProvider.Rest => "Rows are at (path, e.g. data.items)",
            DbProvider.OData => "Rows are at (empty = the value list)",
            _ => "Rows are at"
        };
        _queryHint.Text = provider.IsFile()
            ? "The report reads its rows from the file above. A name without a folder is looked up in the folder of the report file. " +
              "Files up to 50 MB are read. Use Test && load fields to see the fields and fill the field list."
            : provider.IsWeb()
                ? "The report reads its rows from the web address above, as JSON. Write {ParameterName} in the address to pass a report parameter (it is URL-encoded). " +
                  "Only https addresses are read, not addresses on this computer or the local network. Put keys in the headers box, not in the address. " +
                  "Use Test && load fields to see the fields and fill the field list."
                : "SQL query (use @ParameterName to pass report parameters safely as database parameters):";
    }

    private void BrowseForSource()
    {
        var provider = (DbProvider)Math.Max(0, _provider.SelectedIndex);
        using var dlg = new OpenFileDialog
        {
            Title = "Choose the data file",
            Filter = provider switch
            {
                DbProvider.Json => "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DbProvider.Xml => "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                DbProvider.Csv => "CSV and text files (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|All files (*.*)|*.*",
                DbProvider.Excel => "Excel workbooks (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                _ => "All files (*.*)|*.*"
            },
            InitialDirectory = _baseDirectory != null && Directory.Exists(_baseDirectory) ? _baseDirectory : ""
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _source.Text = RelativeToReport(dlg.FileName);
    }

    /// <summary>A file inside the report's folder is stored by its short name, so the report and its data can be moved together.</summary>
    private string RelativeToReport(string fullPath)
    {
        if (string.IsNullOrEmpty(_baseDirectory)) return fullPath;
        var relative = Path.GetRelativePath(_baseDirectory, fullPath);
        bool outside = relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative);
        return outside ? fullPath : relative;
    }

    private static string HeadersToCell(string headers) =>
        string.Join(" | ", (headers ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static string CellToHeaders(string cell) =>
        string.Join("\n", (cell ?? "").Split('|', '\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Reads the first rows of a file or web address and offers its fields (with their types) as the report's field list.</summary>
    private void TestOutsideSource(DataSourceDefinition ds)
    {
        var settings = new SourceSettings { BaseDirectory = _baseDirectory };
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _def.Parameters)
            parameters[p.Name] = (p.DefaultValue ?? "").StartsWith('=') ? "" : p.DefaultValue ?? "";   // the address may use {Name}: try it with the default
        try
        {
            Cursor = Cursors.WaitCursor;
            var fields = DataLoader.DetectFields(ds, settings, parameters);
            if (fields.Count == 0)
            {
                MessageBox.Show(this, "The source was read, but it has no rows, so no fields could be found.",
                    "Test source", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var shown = string.Join(", ", fields.Take(40).Select(f => $"{f.Name} ({f.Type})")) + (fields.Count > 40 ? ", ..." : "");
            var use = MessageBox.Show(this, $"The source was read. It has {fields.Count} fields:\n\n{shown}\n\nUse these as the report's field list?",
                "Test source", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (use == DialogResult.Yes) _fields.Text = string.Join(Environment.NewLine, fields.Select(f => f.ToString()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "The source could not be read", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void TestConnection()
    {
        var ds = new DataSourceDefinition
        {
            Provider = (DbProvider)Math.Max(0, _provider.SelectedIndex),
            ConnectionString = _connection.Text.Trim(),
            Query = _query.Text,
            CommandTimeoutSeconds = (int)_timeout.Value,
            Source = _source.Text.Trim(),
            Selector = _selector.Text.Trim(),
            Delimiter = _delimiter.Text,
            HasHeaderRow = _hasHeader.Checked,
            Headers = CellToHeaders(_headers.Text)
        };
        if (ds.Provider.IsFile() || ds.Provider.IsWeb()) { TestOutsideSource(ds); return; }
        try
        {
            Cursor = Cursors.WaitCursor;
            DataLoader.TestConnection(ds);
            var fields = DataLoader.GetFieldNames(ds);
            MessageBox.Show(this,
                fields.Count == 0 ? "Connected. Enter a query to see its fields."
                                  : "Connected. The query returns:\n\n" + string.Join(", ", fields),
                "Test connection", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Lets the user pick the watermark picture; only a picture the report may print (or a plain SVG drawing) is taken.</summary>
    private void ChooseWatermarkPicture()
    {
        using var dlg = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.svg|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            if (bytes.Length > 5 * 1024 * 1024)
            {
                MessageBox.Show(this, "This picture is larger than 5 MB. A watermark is kept inside the report file, so choose a smaller picture.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!ImageData.IsPicture(bytes))
            {
                MessageBox.Show(this, "This file cannot be used as a watermark picture. Choose a PNG, JPEG, GIF, BMP or TIFF picture, " +
                    "or a plain SVG drawing (no scripts or links to other files or web addresses).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _wmImage = Convert.ToBase64String(bytes);
            ShowWatermarkPicture();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowWatermarkPicture() =>
        _wmPictureName.Text = _wmImage.Length == 0 ? "(none)" : $"picture set ({Math.Max(1, _wmImage.Length * 3L / 4 / 1024)} KB)";

    private static void AddRow(TableLayoutPanel t, string label, Control input)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
        input.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(input);
    }

    /// <summary>A row whose label and input are shown or hidden together with the other rows of the same group.</summary>
    private static Label AddRow(TableLayoutPanel t, string label, Control input, List<Control> group)
    {
        var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
        input.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(caption);
        t.Controls.Add(input);
        group.Add(caption);
        group.Add(input);
        return caption;
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
    };

    private static NumericUpDown NewMargin() => new() { Minimum = 0, Maximum = 400, DecimalPlaces = 1, Width = 80 };
}
