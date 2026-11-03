using System.ComponentModel;
using ReportStudio.Core.Model;
using ReportStudio.Export;

namespace ReportStudio.Designer;

/// <summary>
/// Edits the conditional formatting of one element: a list of rules, each with a condition and the colours and font
/// styles it applies. Rules are checked top to bottom and every true rule applies, so a lower rule wins over a higher one.
/// </summary>
public sealed class RulesEditorDialog : Form
{
    private const string NoChange = "No change", Yes = "Yes", No = "No";

    private readonly List<FormatRule> _rules;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _condition = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10f) };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _fore = NewColorBox(), _back = NewColorBox(), _border = NewColorBox();
    private readonly Panel _foreSwatch = NewSwatch(), _backSwatch = NewSwatch(), _borderSwatch = NewSwatch();
    private readonly ComboBox _bold = NewTriState(), _italic = NewTriState(), _underline = NewTriState();
    private readonly Panel _details = new() { Dock = DockStyle.Fill, Enabled = false };
    private bool _loading;

    public RulesEditorDialog(IEnumerable<FormatRule> rules)
    {
        _rules = rules.Select(r => r.Clone()).ToList();

        Text = "Conditional formatting";
        Size = new Size(860, 520);
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;

        var ok = new Button { Text = "OK", Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0),
            Text = "Each rule changes how the element looks when its condition is true, for example red text when Net < 0. " +
                   "Rules apply from the top; a lower rule wins where two change the same setting."
        };

        // left: the list of rules
        var add = new Button { Text = "Add", Width = 64 };
        var remove = new Button { Text = "Delete", Width = 64 };
        var up = new Button { Text = "Up", Width = 64 };
        var down = new Button { Text = "Down", Width = 64 };
        add.Click += (_, _) => AddRule();
        remove.Click += (_, _) => RemoveRule();
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        listButtons.Controls.AddRange(new Control[] { add, remove, up, down });
        var left = new Panel { Dock = DockStyle.Left, Width = 260, Padding = new Padding(10, 6, 6, 6) };
        left.Controls.Add(_list);
        left.Controls.Add(listButtons);

        // right: the selected rule
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var conditionFx = new Button { Text = "ƒx…", Dock = DockStyle.Right, Width = 64 };
        conditionFx.Click += (_, _) => EditCondition();
        var conditionRow = new Panel { Dock = DockStyle.Fill, Height = 26 };
        conditionRow.Controls.Add(_condition);
        conditionRow.Controls.Add(conditionFx);

        AddRow(table, "When this is true", conditionRow);
        AddRow(table, "", _status);
        AddRow(table, "Text colour", ColorRow(_fore, _foreSwatch));
        AddRow(table, "Fill colour", ColorRow(_back, _backSwatch));
        AddRow(table, "Border colour", ColorRow(_border, _borderSwatch));
        AddRow(table, "Bold", _bold);
        AddRow(table, "Italic", _italic);
        AddRow(table, "Underline", _underline);
        _details.Controls.Add(table);

        Controls.Add(_details);
        Controls.Add(left);
        Controls.Add(hint);
        Controls.Add(buttons);
        AcceptButton = null;
        CancelButton = cancel;

        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        _condition.TextChanged += (_, _) => Store();
        foreach (var box in new[] { _fore, _back, _border })
            box.TextChanged += (_, _) => Store();
        foreach (var combo in new[] { _bold, _italic, _underline })
            combo.SelectedIndexChanged += (_, _) => Store();

        FillList();
        if (_rules.Count > 0) _list.SelectedIndex = 0;
    }

    /// <summary>The edited rules.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<FormatRule> Rules => _rules;

    // ---- the list ----

    private void FillList()
    {
        _loading = true;
        int keep = _list.SelectedIndex;
        _list.Items.Clear();
        for (int i = 0; i < _rules.Count; i++) _list.Items.Add(Describe(i));
        if (keep >= 0 && keep < _list.Items.Count) _list.SelectedIndex = keep;
        _loading = false;
    }

    private string Describe(int index) =>
        $"{index + 1}.  " + (string.IsNullOrWhiteSpace(_rules[index].Condition) ? "(no condition yet)" : _rules[index].Condition.Trim());

    private void AddRule()
    {
        _rules.Add(new FormatRule { ForeColor = "#B91C1C" });
        FillList();
        _list.SelectedIndex = _rules.Count - 1;
        _condition.Focus();
    }

    private void RemoveRule()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        _rules.RemoveAt(i);
        FillList();
        if (_rules.Count > 0) _list.SelectedIndex = Math.Min(i, _rules.Count - 1);
        else LoadSelected();
    }

    private void Move(int by)
    {
        int i = _list.SelectedIndex, j = i + by;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        FillList();
        _list.SelectedIndex = j;
    }

    // ---- the selected rule ----

    private void LoadSelected()
    {
        if (_loading) return;
        _loading = true;
        int i = _list.SelectedIndex;
        _details.Enabled = i >= 0;
        var rule = i >= 0 ? _rules[i] : new FormatRule();

        _condition.Text = rule.Condition;
        _fore.Text = rule.ForeColor;
        _back.Text = rule.BackColor;
        _border.Text = rule.BorderColor;
        SetTriState(_bold, rule.Bold);
        SetTriState(_italic, rule.Italic);
        SetTriState(_underline, rule.Underline);
        ShowSwatches();
        ShowStatus();
        _loading = false;
    }

    private void Store()
    {
        if (_loading) return;
        int i = _list.SelectedIndex;
        if (i < 0) return;

        var rule = _rules[i];
        rule.Condition = _condition.Text.Trim();
        rule.ForeColor = _fore.Text.Trim();
        rule.BackColor = _back.Text.Trim();
        rule.BorderColor = _border.Text.Trim();
        rule.Bold = GetTriState(_bold);
        rule.Italic = GetTriState(_italic);
        rule.Underline = GetTriState(_underline);

        ShowSwatches();
        ShowStatus();
        _loading = true;
        _list.Items[i] = Describe(i);
        _loading = false;
    }

    private void ShowStatus()
    {
        var problem = DefinitionChecks.CheckExpression(_condition.Text);
        _status.ForeColor = problem == null ? SystemColors.GrayText : Color.Firebrick;
        _status.Text = problem != null ? "Not valid: " + problem
            : string.IsNullOrWhiteSpace(_condition.Text) ? "Example: Net < 0" : "The condition is valid.";
    }

    private void ShowSwatches()
    {
        _foreSwatch.BackColor = ColorUtil.Parse(_fore.Text) ?? SystemColors.Control;
        _backSwatch.BackColor = ColorUtil.Parse(_back.Text) ?? SystemColors.Control;
        _borderSwatch.BackColor = ColorUtil.Parse(_border.Text) ?? SystemColors.Control;
    }

    private void EditCondition()
    {
        using var dlg = new ExpressionEditorDialog("Rule condition", _condition.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) _condition.Text = dlg.Expression;
    }

    private void Accept()
    {
        var problems = DefinitionChecks.CheckRules(_rules);
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(8)), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }

    // ---- small helpers ----

    private Control ColorRow(TextBox box, Panel swatch)
    {
        var pick = new Button { Text = "Pick…", Dock = DockStyle.Right, Width = 64 };
        pick.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true };
            var current = ColorUtil.Parse(box.Text);
            if (current != null) dlg.Color = current.Value;
            if (dlg.ShowDialog(this) == DialogResult.OK)
                box.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        };
        var clear = new Button { Text = "Clear", Dock = DockStyle.Right, Width = 54 };
        clear.Click += (_, _) => box.Text = "";

        var row = new Panel { Dock = DockStyle.Fill, Height = 26 };
        row.Controls.Add(box);
        row.Controls.Add(swatch);
        row.Controls.Add(clear);
        row.Controls.Add(pick);
        return row;
    }

    private static TextBox NewColorBox() => new() { Dock = DockStyle.Fill, Width = 120 };

    private static Panel NewSwatch() => new() { Dock = DockStyle.Right, Width = 28, BorderStyle = BorderStyle.FixedSingle };

    private static ComboBox NewTriState()
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        combo.Items.AddRange(new object[] { NoChange, Yes, No });
        combo.SelectedIndex = 0;
        return combo;
    }

    private static void SetTriState(ComboBox combo, bool? value) =>
        combo.SelectedItem = value == null ? NoChange : value.Value ? Yes : No;

    private static bool? GetTriState(ComboBox combo) => combo.SelectedItem as string switch
    {
        Yes => true,
        No => false,
        _ => null
    };

    private static void AddRow(TableLayoutPanel t, string label, Control input)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
        input.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(input);
    }
}
