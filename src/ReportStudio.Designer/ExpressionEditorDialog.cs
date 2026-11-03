using System.ComponentModel;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Model;

namespace ReportStudio.Designer;

/// <summary>
/// Edits one expression: a text box that says at once whether the expression is valid, the list of functions with
/// what each does, and the report's fields, parameters and variables. Double-click an entry to put it in the text.
/// </summary>
public sealed class ExpressionEditorDialog : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 11f),
        AcceptsReturn = true, AcceptsTab = false
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ListBox _functions = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _values = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _help = new()
    {
        Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(4), BorderStyle = BorderStyle.FixedSingle
    };

    private List<FunctionInfo> _shown = new();

    public ExpressionEditorDialog(string title, string expression)
    {
        Text = "Expression: " + title;
        Size = new Size(820, 600);
        MinimumSize = new Size(640, 480);
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

        // top: the expression
        var top = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(10, 10, 10, 4) };
        top.Controls.Add(_text);
        top.Controls.Add(_status);

        // bottom left: functions, bottom right: fields and values
        var functionsBox = new GroupBox { Text = "Functions (double-click to insert)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        functionsBox.Controls.Add(_functions);
        functionsBox.Controls.Add(_help);
        functionsBox.Controls.Add(_category);

        var valuesBox = new GroupBox { Text = "Fields, parameters, variables", Dock = DockStyle.Fill, Padding = new Padding(8) };
        valuesBox.Controls.Add(_values);
        valuesBox.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(0, 4, 0, 0),
            Text = "Compare with == != < <= > >=, combine with && and ||, text in 'single quotes'. " +
                   "A field with spaces is written [Field Name]."
        });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 440 };
        split.Panel1.Controls.Add(functionsBox);
        split.Panel2.Controls.Add(valuesBox);

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(buttons);
        AcceptButton = null;               // Enter belongs to the text box
        CancelButton = cancel;

        _category.Items.Add("All");
        foreach (var c in ExpressionFunctions.Categories) _category.Items.Add(c);
        _category.SelectedIndex = 0;
        _category.SelectedIndexChanged += (_, _) => ShowFunctions();
        _functions.SelectedIndexChanged += (_, _) => ShowHelp();
        _functions.DoubleClick += (_, _) => InsertFunction();
        _values.DoubleClick += (_, _) => InsertValue();

        foreach (var (name, _) in ExpressionFunctions.SpecialValues) _values.Items.Add(name);
        foreach (var v in ExpressionEditorContext.Variables) _values.Items.Add(v);
        foreach (var f in ExpressionEditorContext.Fields) _values.Items.Add(f);

        _text.Text = expression;
        _text.TextChanged += (_, _) => ShowStatus();
        ShowFunctions();
        ShowStatus();
        Shown += (_, _) => { _text.Focus(); _text.SelectionStart = _text.TextLength; };
    }

    /// <summary>The edited expression, trimmed.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Expression => _text.Text.Trim();

    private void Accept()
    {
        var problem = DefinitionChecks.CheckExpression(_text.Text);
        if (problem != null)
        {
            MessageBox.Show(this, "The expression is not valid:\n\n" + problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _text.Focus();
            return;
        }
        DialogResult = DialogResult.OK;
    }

    private void ShowStatus()
    {
        var problem = DefinitionChecks.CheckExpression(_text.Text);
        _status.ForeColor = problem == null ? SystemColors.GrayText : Color.Firebrick;
        _status.Text = problem != null ? "Not valid: " + problem
            : string.IsNullOrWhiteSpace(_text.Text) ? "Type an expression, or double-click a function or field." : "The expression is valid.";
    }

    private void ShowFunctions()
    {
        var category = _category.SelectedIndex <= 0 ? null : (string)_category.SelectedItem!;
        _shown = ExpressionFunctions.All.Where(f => category == null || f.Category == category).ToList();
        _functions.Items.Clear();
        foreach (var f in _shown) _functions.Items.Add(f.Name);
        ShowHelp();
    }

    private void ShowHelp()
    {
        var index = _functions.SelectedIndex;
        _help.Text = index < 0 || index >= _shown.Count
            ? "Pick a function to see what it does."
            : $"{_shown[index].Signature}\n{_shown[index].Description}\nExample: {_shown[index].Example}";
    }

    private void InsertFunction()
    {
        int index = _functions.SelectedIndex;
        if (index < 0 || index >= _shown.Count) return;
        Insert(_shown[index].Name + "()", caretBack: 1);
    }

    private void InsertValue()
    {
        if (_values.SelectedItem is string value) Insert(value, 0);
    }

    private void Insert(string text, int caretBack)
    {
        _text.SelectedText = text;
        _text.SelectionStart = Math.Max(0, _text.SelectionStart - caretBack);
        _text.SelectionLength = 0;
        _text.Focus();
    }
}
