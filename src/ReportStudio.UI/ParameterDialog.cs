using System.Globalization;
using ReportStudio.Core.Data;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.UI;

/// <summary>
/// Asks the user for report parameter values before running a report. Shows each parameter's default (an expression
/// default such as =MonthStart(Today()) is worked out now) so it can be reviewed, and offers drop-down lists, pick-several
/// lists and lists read from the database that follow other parameters.
/// </summary>
public sealed class ParameterDialog : Form
{
    /// <summary>An entry in a drop-down or check list: the value is what the report gets, the label is what the user reads.</summary>
    private sealed record Item(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly List<(ReportParameter Param, Control Input)> _inputs = new();
    private readonly ReportDefinition? _definition;
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(420, 0) };
    private bool _loading;

    public ParameterDialog(IEnumerable<ReportParameter> parameters, IDictionary<string, string?>? previous = null,
        ReportDefinition? definition = null)
    {
        _definition = definition;
        Text = "Report parameters";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var table = new TableLayoutPanel
        {
            AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

        foreach (var p in parameters)
        {
            string initial = p.ResolveDefault();
            if (previous != null && previous.TryGetValue(p.Name, out var prev) && prev != null) initial = prev;

            bool hasList = !string.IsNullOrWhiteSpace(p.Choices) || !string.IsNullOrWhiteSpace(p.ChoicesQuery);
            Control input;
            if (p.MultiValue && hasList)
                input = new CheckedListBox { Width = 260, Height = 96, CheckOnClick = true, IntegralHeight = false, Tag = initial };
            else if (p.MultiValue)
                input = new TextBox { Text = string.Join(", ", ValueList.Split(initial)), Width = 260 };   // typed: Sales, Stores
            else if (hasList)
                input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 260, Text = initial, Tag = initial };
            else
                input = p.Type switch
                {
                    ParameterType.Boolean => new CheckBox { Checked = bool.TryParse(initial, out var b) && b, AutoSize = true },
                    ParameterType.Date => new DateTimePicker
                    {
                        Format = DateTimePickerFormat.Short, Width = 260,
                        Value = DateTime.TryParse(initial, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today
                    },
                    _ => new TextBox { Text = initial, Width = 260 }
                };

            var caption = string.IsNullOrWhiteSpace(p.Prompt) ? p.Name : p.Prompt;
            if (p.MultiValue) caption += hasList ? " (tick any)" : " (separate with commas)";
            table.Controls.Add(new Label
            {
                Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6)
            });
            table.Controls.Add(input);
            _inputs.Add((p, input));
        }

        // A list that depends on other parameters is read again whenever one of them changes.
        foreach (var (p, _) in _inputs)
            foreach (var parent in ParameterChoices.ParseNames(p.DependsOn))
            {
                var source = _inputs.FirstOrDefault(i => string.Equals(i.Param.Name, parent, StringComparison.OrdinalIgnoreCase)).Input;
                if (source != null) Watch(source, () => LoadChoices(p));
            }

        var ok = new Button { Text = "Run report", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        _status.Dock = DockStyle.Bottom;
        Controls.Add(buttons);
        Controls.Add(_status);
        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;

        foreach (var (p, _) in _inputs) LoadChoices(p);   // parents come first in the list, so dependants see their values
    }

    private static void Watch(Control input, Action changed)
    {
        switch (input)
        {
            case CheckedListBox l: l.ItemCheck += (_, _) => l.BeginInvoke(changed); break;
            case ComboBox c: c.SelectedIndexChanged += (_, _) => changed(); c.TextChanged += (_, _) => changed(); break;
            case DateTimePicker d: d.ValueChanged += (_, _) => changed(); break;
            case CheckBox cb: cb.CheckedChanged += (_, _) => changed(); break;
            default: input.TextChanged += (_, _) => changed(); break;
        }
    }

    /// <summary>Fills a parameter's list (fixed, or from the database with the other parameters' current values).</summary>
    private void LoadChoices(ReportParameter p)
    {
        var input = _inputs.First(i => ReferenceEquals(i.Param, p)).Input;
        if (input is not (ComboBox or CheckedListBox) || _loading) return;

        List<ParameterChoice> choices;
        try
        {
            _status.Text = "";
            choices = _definition == null
                ? ParameterChoices.Parse(p.Choices)
                : ParameterChoiceLoader.Load(_definition, p, CurrentValues());
        }
        catch (Exception ex)
        {
            _status.Text = $"The list for {(string.IsNullOrWhiteSpace(p.Prompt) ? p.Name : p.Prompt)} could not be read: {ex.Message}";
            return;
        }

        _loading = true;
        try
        {
            var keep = input.Tag as string ?? CurrentRaw(p, input);   // the starting value the first time, then what was chosen before the list changed
            input.Tag = null;
            switch (input)
            {
                case ComboBox combo:
                    combo.Items.Clear();
                    combo.Items.AddRange(choices.Select(c => (object)new Item(c.Value, c.Label)).ToArray());
                    var match = combo.Items.Cast<Item>().FirstOrDefault(i => string.Equals(i.Value, keep, StringComparison.OrdinalIgnoreCase));
                    if (match != null) combo.SelectedItem = match; else combo.Text = keep;
                    break;
                case CheckedListBox list:
                    var wanted = ValueList.Split(keep);
                    list.Items.Clear();
                    foreach (var c in choices)
                        list.Items.Add(new Item(c.Value, c.Label), wanted.Any(w => string.Equals(w, c.Value, StringComparison.OrdinalIgnoreCase)));
                    break;
            }
        }
        finally { _loading = false; }

        // A list that follows this one may now have another set of values.
        foreach (var (other, _) in _inputs)
            if (ParameterChoices.ParseNames(other.DependsOn).Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)))
                LoadChoices(other);
    }

    /// <summary>The values typed or chosen so far, as the report would receive them; one that cannot be read yet counts as empty.</summary>
    private Dictionary<string, object?> CurrentValues()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, input) in _inputs)
        {
            try { result[p.Name] = p.Parse(CurrentRaw(p, input)); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { result[p.Name] = null; }
        }
        return result;
    }

    private static string CurrentRaw(ReportParameter p, Control input) => input switch
    {
        CheckBox cb => cb.Checked ? "true" : "false",
        DateTimePicker dt => dt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        CheckedListBox list => ValueList.Join(list.CheckedItems.Cast<Item>().Select(i => i.Value)),
        ComboBox combo => combo.SelectedItem is Item chosen ? chosen.Value : ShownToValue(combo),
        TextBox box when p.MultiValue => ValueList.Join(box.Text.Split(',')),
        _ => input.Text
    };

    /// <summary>Text typed in a list box: when it is the shown text of an entry, the entry's value; otherwise what was typed.</summary>
    private static string ShownToValue(ComboBox combo)
    {
        var typed = combo.Text.Trim();
        var entry = combo.Items.Cast<Item>().FirstOrDefault(i => string.Equals(i.Label, typed, StringComparison.OrdinalIgnoreCase));
        return entry?.Value ?? typed;
    }

    /// <summary>Raw string values keyed by parameter name (dates in invariant yyyy-MM-dd, several values joined by |).</summary>
    [System.ComponentModel.Browsable(false),
     System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Dictionary<string, string?> Values
    {
        get
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, input) in _inputs) result[p.Name] = CurrentRaw(p, input);
            return result;
        }
    }
}
