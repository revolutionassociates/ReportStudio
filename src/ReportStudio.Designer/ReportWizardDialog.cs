using ReportStudio.Core.Model;
using ReportStudio.Core.Wizard;

namespace ReportStudio.Designer;

/// <summary>
/// The new-report wizard (requirement 45b): the data fields, which to show and in what order, group and total fields, and the look.
/// A problem found by <see cref="ReportWizard.Check"/> is shown at the bottom and keeps OK disabled.
/// </summary>
public sealed class ReportWizardDialog : Form
{
    private const string None = "(none)";

    private DataSourceDefinition _source;
    private ReportDefinition? _report;
    private bool _busy;

    private readonly Label _sourceLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox _fieldLines = new()
    {
        Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false
    };
    private readonly CheckedListBox _columns = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox[] _groupBy = { NewCombo(), NewCombo(), NewCombo() };
    private readonly CheckedListBox _totals = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _title = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _layout = NewCombo();
    private readonly ComboBox _style = NewCombo();
    private readonly ComboBox _paper = NewCombo();
    private readonly CheckBox _landscape = new() { Text = "Landscape", AutoSize = true };
    private readonly CheckBox _pageNumbers = new() { Text = "Page numbers", AutoSize = true, Checked = true };
    private readonly CheckBox _printedDate = new() { Text = "Printed date", AutoSize = true, Checked = true };
    private readonly CheckBox _grandTotal = new() { Text = "Total at the end", AutoSize = true, Checked = true };
    private readonly Label _problems = new() { Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.Firebrick, AutoEllipsis = true };
    private readonly Button _ok = new() { Text = "Create", Width = 90 };

    /// <param name="source">A copy of the data source the new report starts with (the dialog changes its field list).</param>
    public ReportWizardDialog(DataSourceDefinition source, string suggestedName = "New report")
    {
        Load += (_, _) => DesignerUi.Apply(this);
        _source = source;
        Text = "New report wizard";
        Size = new Size(880, 660);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        _name.Text = suggestedName;
        _layout.Items.AddRange(new object[] { "Table - a line for each record", "Form - a block for each record" });
        _style.Items.AddRange(new object[] { "Corporate (blue)", "Simple (grey)", "Modern (teal)", "Classic (black lines)" });
        _paper.Items.AddRange(new object[] { "A4", "Letter" });
        _layout.SelectedIndex = 0;
        _style.SelectedIndex = 0;
        _paper.SelectedIndex = 0;

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(8) };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        main.Controls.Add(BuildLeft(), 0, 0);
        main.Controls.Add(BuildRight(), 1, 0);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        _ok.Click += (_, _) => Create();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        // The one added last is docked first, so the buttons end up lowest, the problem line above them.
        Controls.Add(main);
        Controls.Add(_problems);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        _fieldLines.TextChanged += (_, _) => RefreshFields();
        _columns.ItemCheck += (_, _) => Changed();
        _totals.ItemCheck += (_, _) => Changed();
        foreach (var combo in _groupBy.Append(_layout).Append(_style).Append(_paper)) combo.SelectedIndexChanged += (_, _) => Changed();
        foreach (var box in new[] { _landscape, _pageNumbers, _printedDate, _grandTotal }) box.CheckedChanged += (_, _) => Changed();
        _name.TextChanged += (_, _) => Changed();
        _title.TextChanged += (_, _) => Changed();

        if (_source.Fields.Count == 0 && _source.Provider == DbProvider.Sample)
            _source.Fields = WizardOptions.ForSampleData().Fields;
        _fieldLines.Text = string.Join(Environment.NewLine, _source.Fields.Select(f => f.ToString()));
        UpdateSourceLabel();
        RefreshFields();
    }

    /// <summary>The finished report after OK, otherwise null.</summary>
    public ReportDefinition? TakeReport() => _report;

    // ---- layout ---------------------------------------------------------------------------------

    private Control BuildLeft()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sample = new Button { Text = "Use the sample data", AutoSize = true };
        sample.Click += (_, _) => UseSample();
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 32,
            Text = "One field per line: Name for text, or Name:Number, Name:Date, Name:Boolean."
        };
        var dataInner = new Panel { Dock = DockStyle.Fill };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        top.Controls.Add(sample);
        top.Controls.Add(_sourceLabel);
        dataInner.Controls.Add(_fieldLines);
        dataInner.Controls.Add(hint);
        dataInner.Controls.Add(top);
        panel.Controls.Add(Group("1. Data", dataInner), 0, 0);

        var up = new Button { Text = "Up", Width = 70 };
        var down = new Button { Text = "Down", Width = 70 };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        var order = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight };
        order.Controls.Add(up);
        order.Controls.Add(down);
        var fieldsInner = new Panel { Dock = DockStyle.Fill };
        fieldsInner.Controls.Add(_columns);
        fieldsInner.Controls.Add(order);
        panel.Controls.Add(Group("2. Fields to show (tick them, order them with Up and Down)", fieldsInner), 0, 1);
        return panel;
    }

    private Control BuildRight()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));

        var groups = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        groups.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        groups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] labels = { "Group by", "then by", "then by" };
        for (int i = 0; i < 3; i++)
        {
            groups.Controls.Add(new Label { Text = labels[i], AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, i);
            groups.Controls.Add(_groupBy[i], 1, i);
        }
        panel.Controls.Add(Group("3. Group the records", groups), 0, 0);
        panel.Controls.Add(Group("4. Add up these number fields (in every group and at the end)", _totals), 0, 1);

        var look = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        look.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        look.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(look, 0, "Report name", _name);
        AddRow(look, 1, "Heading", _title);
        AddRow(look, 2, "Layout", _layout);
        AddRow(look, 3, "Style", _style);
        AddRow(look, 4, "Paper", _paper);
        var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        checks.Controls.AddRange(new Control[] { _landscape, _pageNumbers, _printedDate, _grandTotal });
        look.Controls.Add(checks, 1, 5);
        panel.Controls.Add(Group("5. Look", look), 0, 2);
        return panel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    private static ComboBox NewCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

    // ---- state ----------------------------------------------------------------------------------

    private List<FieldDefinition> ParsedFields() => FieldDefinition.ParseLines(_fieldLines.Lines);

    private void UseSample()
    {
        _source = new DataSourceDefinition { Provider = DbProvider.Sample };
        _fieldLines.Text = string.Join(Environment.NewLine, WizardOptions.ForSampleData().Fields.Select(f => f.ToString()));
        UpdateSourceLabel();
        RefreshFields();
    }

    private void UpdateSourceLabel() => _sourceLabel.Text = "Data: " + _source.Provider;

    /// <summary>Fills the field lists again after the field text changed, keeping what was ticked, chosen and the order of known fields.</summary>
    private void RefreshFields()
    {
        _busy = true;
        try
        {
            var fields = ParsedFields();
            var names = fields.Select(f => f.Name).ToList();

            var shown = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            for (int i = 0; i < _columns.Items.Count; i++)
            {
                var existing = _columns.Items[i]?.ToString() ?? "";
                shown[existing] = _columns.GetItemChecked(i);
                order.Add(existing);
            }
            var kept = order.Where(o => names.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
            kept.AddRange(names.Where(n => !kept.Contains(n, StringComparer.OrdinalIgnoreCase)));

            _columns.Items.Clear();
            foreach (var n in kept) _columns.Items.Add(n, shown.TryGetValue(n, out var on) ? on : true);

            var summed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _totals.Items.Count; i++)
                if (_totals.GetItemChecked(i)) summed.Add(_totals.Items[i]?.ToString() ?? "");
            _totals.Items.Clear();
            foreach (var f in fields.Where(f => f.Type == FieldType.Number))
                _totals.Items.Add(f.Name, summed.Contains(f.Name));

            foreach (var combo in _groupBy)
            {
                var previous = combo.SelectedItem?.ToString();
                combo.Items.Clear();
                combo.Items.Add(None);
                foreach (var n in names) combo.Items.Add(n);
                int at = previous == null ? -1 : combo.Items.IndexOf(previous);
                combo.SelectedIndex = at >= 0 ? at : 0;
            }
        }
        finally
        {
            _busy = false;
        }
        Changed();
    }

    private void Move(int delta)
    {
        int from = _columns.SelectedIndex, to = from + delta;
        if (from < 0 || to < 0 || to >= _columns.Items.Count) return;
        _busy = true;
        try
        {
            var item = _columns.Items[from];
            bool on = _columns.GetItemChecked(from);
            _columns.Items.RemoveAt(from);
            _columns.Items.Insert(to, item);
            _columns.SetItemChecked(to, on);
            _columns.SelectedIndex = to;
        }
        finally
        {
            _busy = false;
        }
        Changed();
    }

    private void Changed()
    {
        if (_busy) return;
        // ItemCheck fires before the box is ticked, so the check runs once the click is finished.
        if (IsHandleCreated) BeginInvoke(new Action(Revalidate));
        else Revalidate();
    }

    private WizardOptions BuildOptions()
    {
        var fields = ParsedFields();
        _source.Fields = fields.Select(f => new FieldDefinition { Name = f.Name, Type = f.Type }).ToList();

        var shown = new List<string>();
        for (int i = 0; i < _columns.Items.Count; i++)
            if (_columns.GetItemChecked(i)) shown.Add(_columns.Items[i]?.ToString() ?? "");

        var summed = new List<string>();
        for (int i = 0; i < _totals.Items.Count; i++)
            if (_totals.GetItemChecked(i)) summed.Add(_totals.Items[i]?.ToString() ?? "");

        var grouped = _groupBy.Select(c => c.SelectedItem?.ToString() ?? None).Where(n => n != None).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new WizardOptions
        {
            Name = _name.Text.Trim(),
            Title = _title.Text.Trim(),
            DataSource = _source,
            Fields = fields,
            Columns = shown,
            GroupBy = grouped,
            TotalFields = summed,
            Layout = _layout.SelectedIndex == 1 ? WizardLayout.Form : WizardLayout.Table,
            Style = (WizardStyle)Math.Max(0, _style.SelectedIndex),
            Paper = _paper.SelectedIndex == 1 ? WizardPaper.Letter : WizardPaper.A4,
            Landscape = _landscape.Checked,
            PageNumbers = _pageNumbers.Checked,
            PrintedDate = _printedDate.Checked,
            GrandTotal = _grandTotal.Checked
        };
    }

    private void Revalidate()
    {
        if (IsDisposed) return;
        var options = BuildOptions();
        var problems = new List<string>(ReportWizard.Check(options));
        if (options.Fields.Count > 0 && options.Columns.Count == 0) problems.Insert(0, "Tick at least one field to show.");
        _problems.Text = problems.Count == 0 ? "" : problems[0];
        _ok.Enabled = problems.Count == 0;
    }

    private void Create()
    {
        try
        {
            _report = ReportWizard.Build(BuildOptions());
            DialogResult = DialogResult.OK;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
