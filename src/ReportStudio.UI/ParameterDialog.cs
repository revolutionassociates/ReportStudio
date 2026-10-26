using System.Globalization;
using ReportStudio.Core.Model;

namespace ReportStudio.UI;

/// <summary>Asks the user for report parameter values before running a report.</summary>
public sealed class ParameterDialog : Form
{
    private readonly List<(ReportParameter Param, Control Input)> _inputs = new();

    public ParameterDialog(IEnumerable<ReportParameter> parameters, IDictionary<string, string?>? previous = null)
    {
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
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));

        foreach (var p in parameters)
        {
            string initial = p.DefaultValue;
            if (previous != null && previous.TryGetValue(p.Name, out var prev) && prev != null) initial = prev;

            Control input = p.Type switch
            {
                ParameterType.Boolean => new CheckBox { Checked = bool.TryParse(initial, out var b) && b, AutoSize = true },
                ParameterType.Date => new DateTimePicker
                {
                    Format = DateTimePickerFormat.Short, Width = 240,
                    Value = DateTime.TryParse(initial, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today
                },
                _ => new TextBox { Text = initial, Width = 240 }
            };

            table.Controls.Add(new Label
            {
                Text = string.IsNullOrWhiteSpace(p.Prompt) ? p.Name : p.Prompt,
                AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6)
            });
            table.Controls.Add(input);
            _inputs.Add((p, input));
        }

        var ok = new Button { Text = "Run report", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(buttons);
        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Raw string values keyed by parameter name (dates in invariant yyyy-MM-dd).</summary>
    public Dictionary<string, string?> Values
    {
        get
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, input) in _inputs)
            {
                result[p.Name] = input switch
                {
                    CheckBox cb => cb.Checked ? "true" : "false",
                    DateTimePicker dt => dt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => input.Text
                };
            }
            return result;
        }
    }
}
