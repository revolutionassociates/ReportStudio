namespace ReportStudio.Designer;

/// <summary>Asks for one line of text, for example the name of a new template. OK stays disabled while the box is empty.</summary>
public sealed class NamePromptDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Top };
    private readonly Button _ok = new() { Text = "OK", Width = 90 };

    public NamePromptDialog(string title, string prompt, string initial = "")
    {
        Load += (_, _) => DesignerUi.Apply(this);
        Text = title;
        Size = new Size(420, 170);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 24 };
        _box.Text = initial;
        _box.SelectAll();

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        _ok.DialogResult = DialogResult.OK;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        body.Controls.Add(_box);
        body.Controls.Add(label);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        _box.TextChanged += (_, _) => _ok.Enabled = _box.Text.Trim().Length > 0;
        _ok.Enabled = _box.Text.Trim().Length > 0;
    }

    /// <summary>The text typed, without spaces at either end.</summary>
    public string EnteredText() => _box.Text.Trim();
}
