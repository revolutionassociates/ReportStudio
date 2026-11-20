using ReportStudio.Core.Server;

namespace ReportStudio.Designer;

/// <summary>
/// Lists the saved versions of a report, newest first; Restore hands the text of the chosen version back to the designer, which opens it
/// as an unsaved change (requirement 51b). Nothing is deleted or overwritten here: restoring and then saving makes a new version.
/// </summary>
public sealed class VersionHistoryDialog : Form
{
    private readonly ReportHistory _history;
    private readonly string _id;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill
    };
    private readonly Button _restore = new() { Text = "Restore this version", AutoSize = true, Enabled = false };

    /// <summary>The text of the version the person chose, or null when the dialog was closed without restoring.</summary>
    public string? RestoredText { get; private set; }

    public VersionHistoryDialog(ReportHistory history, string id)
    {
        Load += (_, _) => DesignerUi.Apply(this);
        _history = history;
        _id = id;
        Text = "Version history - " + id;
        Size = new Size(700, 440);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _list.Columns.Add("Version", 70);
        _list.Columns.Add("Saved", 140);
        _list.Columns.Add("By", 110);
        _list.Columns.Add("Note", 270);
        _list.Columns.Add("Size", 70, HorizontalAlignment.Right);

        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_restore);

        Controls.Add(_list);
        Controls.Add(buttons);
        CancelButton = close;

        _list.SelectedIndexChanged += (_, _) => _restore.Enabled = _list.SelectedItems.Count == 1;
        _list.DoubleClick += (_, _) => RestoreSelected();
        _restore.Click += (_, _) => RestoreSelected();

        Fill();
    }

    private void Fill()
    {
        IReadOnlyList<ReportVersion> versions;
        try { versions = _history.List(_id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, "The version history could not be read:\n" + ex.Message, "Version history", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        foreach (var v in versions)
        {
            var item = new ListViewItem("v" + v.Number) { Tag = v };
            item.SubItems.Add(v.SavedAt.ToLocalTime().ToString("dd-MMM-yyyy HH:mm"));
            item.SubItems.Add(v.Author);
            item.SubItems.Add(v.Note);
            item.SubItems.Add(Math.Max(1, (v.Bytes + 1023) / 1024) + " KB");
            _list.Items.Add(item);
        }
        if (versions.Count == 0) Text += " (nothing saved yet)";
    }

    private void RestoreSelected()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not ReportVersion version) return;
        try
        {
            var text = _history.Read(_id, version.Number);
            if (text == null) return;
            RestoredText = text;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Version {version.Number} could not be read:\n{ex.Message}", "Version history", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
