using System.Text.RegularExpressions;
using ReportStudio.Core.Data;
using ReportStudio.Core.Model;

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

    // others
    private readonly DataGridView _params = NewGrid();
    private readonly DataGridView _groups = NewGrid();
    private readonly TextBox _sort = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _name = new() { Width = 300 };
    private readonly ComboBox _pageSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly NumericUpDown _mL = NewMargin(), _mR = NewMargin(), _mT = NewMargin(), _mB = NewMargin();

    private static readonly (string Label, double W, double H)[] PageSizes =
    {
        ("A4 portrait", 794, 1123), ("A4 landscape", 1123, 794),
        ("Letter portrait", 816, 1056), ("Letter landscape", 1056, 816),
        ("A5 portrait", 559, 794), ("A5 landscape", 794, 559)
    };

    public ReportSettingsDialog(ReportDefinition def)
    {
        _def = def;
        Text = "Report settings";
        Size = new Size(760, 600);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildDataTab());
        tabs.TabPages.Add(BuildParametersTab());
        tabs.TabPages.Add(BuildGroupsTab());

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
        page.Controls.Add(t);
        return page;
    }

    private TabPage BuildDataTab()
    {
        var page = new TabPage("Data source") { Padding = new Padding(12) };
        _provider.Items.AddRange(new object[]
        {
            "Sample data (no database)", "SQL Server", "MySQL / MariaDB", "PostgreSQL",
            "Host application (rows supplied by the host)"
        });
        _provider.SelectedIndexChanged += (_, _) => UpdateHostState();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(top, "Provider", _provider);
        AddRow(top, "Connection string", _connection);
        AddRow(top, "Command timeout (s)", _timeout);

        var test = new Button { Text = "Test connection && load fields", AutoSize = true };
        test.Click += (_, _) => TestConnection();

        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 4),
            Text = "SQL query (use @ParameterName to pass report parameters safely as database parameters):"
        };

        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_query);
        body.Controls.Add(hint);
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
        _params.Columns.Add("Default", "Default value");
        page.Controls.Add(_params);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6),
            Text = "Use a parameter in the SQL as @Name and on the report as {@Name}. Dates are yyyy-MM-dd."
        });
        return page;
    }

    private TabPage BuildGroupsTab()
    {
        var page = new TabPage("Grouping && sort") { Padding = new Padding(8) };
        _groups.Columns.Add("Expression", "Group by (field or expression)");
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "NewPage", HeaderText = "Start each group on a new page" });
        _groups.Columns.Add(new DataGridViewCheckBoxColumn { Name = "RestartPages", HeaderText = "Restart page numbers" });

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

        _provider.SelectedIndex = (int)_def.DataSource.Provider;
        UpdateHostState();
        _connection.Text = _def.DataSource.ConnectionString;
        _query.Text = _def.DataSource.Query.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _timeout.Value = Math.Clamp(_def.DataSource.CommandTimeoutSeconds, 5, 3600);
        _fields.Text = string.Join(Environment.NewLine, _def.DataSource.Fields.Select(f => f.ToString()));

        foreach (var p in _def.Parameters) _params.Rows.Add(p.Name, p.Prompt, p.Type, p.DefaultValue);
        foreach (var g in _def.Groups) _groups.Rows.Add(g.Expression, g.StartNewPage, g.RestartPageNumbers);
        _sort.Text = string.Join(Environment.NewLine, _def.SortBy);
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
                DefaultValue = Convert.ToString(row.Cells[3].Value) ?? ""
            });
        }

        _def.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled Report" : _name.Text.Trim();
        var size = PageSizes[Math.Max(0, _pageSize.SelectedIndex)];
        _def.Page.Width = size.W;
        _def.Page.Height = size.H;
        _def.Page.MarginLeft = (double)_mL.Value;
        _def.Page.MarginRight = (double)_mR.Value;
        _def.Page.MarginTop = (double)_mT.Value;
        _def.Page.MarginBottom = (double)_mB.Value;

        _def.DataSource.Provider = (DbProvider)Math.Max(0, _provider.SelectedIndex);
        bool host = _def.DataSource.Provider == DbProvider.Host;
        // A Host report never stores a query or connection string.
        _def.DataSource.ConnectionString = host ? "" : _connection.Text.Trim();
        _def.DataSource.Query = host ? "" : _query.Text;
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
            groups.Add(g);
        }
        _def.Groups = groups;
        _def.SortBy = _sort.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _def.Normalize();
        return true;
    }

    private void UpdateHostState()
    {
        bool host = _provider.SelectedIndex == (int)DbProvider.Host;
        _connection.Enabled = _query.Enabled = _timeout.Enabled = !host;
        if (host) { _connection.Text = ""; _query.Text = ""; }
    }

    private void TestConnection()
    {
        var ds = new DataSourceDefinition
        {
            Provider = (DbProvider)Math.Max(0, _provider.SelectedIndex),
            ConnectionString = _connection.Text.Trim(),
            Query = _query.Text,
            CommandTimeoutSeconds = (int)_timeout.Value
        };
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

    private static void AddRow(TableLayoutPanel t, string label, Control input)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
        input.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(input);
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
    };

    private static NumericUpDown NewMargin() => new() { Minimum = 0, Maximum = 400, Width = 80 };
}
