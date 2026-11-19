using ReportStudio.Core.Wizard;

namespace ReportStudio.Designer;

/// <summary>Lists the report templates by category with a short description; OK (or a double click) picks one (requirement 45b).</summary>
public sealed class NewFromTemplateDialog : Form
{
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.None, ShowGroups = true, Dock = DockStyle.Fill
    };
    private readonly Label _description = new() { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(4, 8, 4, 0) };
    private readonly Button _ok = new() { Text = "Create", Width = 90, Enabled = false };

    private readonly string? _userFolder;
    private readonly Button _delete = new() { Text = "Delete my template", AutoSize = true, Enabled = false };

    /// <param name="userFolder">The folder of the person's own templates (listed under "My templates"); null = only the built-in ones.</param>
    public NewFromTemplateDialog(string? userFolder = null)
    {
        _userFolder = userFolder;
        Text = "New report from a template";
        Size = new Size(520, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        _list.Columns.Add("Template", 470);
        var groups = new Dictionary<string, ListViewGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in ReportTemplates.All.Concat(UserTemplates.List(userFolder)))
        {
            if (!groups.TryGetValue(template.Category, out var group))
            {
                group = new ListViewGroup(template.Category);
                groups[template.Category] = group;
                _list.Groups.Add(group);
            }
            _list.Items.Add(new ListViewItem(template.Name) { Group = group, Tag = template });
        }

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        _ok.Click += (_, _) => { if (SelectedTemplate() != null) DialogResult = DialogResult.OK; };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(_delete);
        _delete.Click += (_, _) => DeleteSelected();

        // The one added last is docked first: buttons lowest, then the description, then the list fills the rest.
        Controls.Add(_list);
        Controls.Add(_description);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        _list.SelectedIndexChanged += (_, _) => ShowSelection();
        _list.DoubleClick += (_, _) => { if (SelectedTemplate() != null) DialogResult = DialogResult.OK; };

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    /// <summary>The template chosen in the list, or null when none is selected.</summary>
    public ReportTemplate? SelectedTemplate() =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ReportTemplate : null;

    private void ShowSelection()
    {
        var template = SelectedTemplate();
        _description.Text = template?.Description ?? "";
        _ok.Enabled = template != null;
        _delete.Enabled = template != null && _userFolder != null && UserTemplates.IsUser(template);
    }

    private void DeleteSelected()
    {
        var template = SelectedTemplate();
        if (template == null || _userFolder == null || !UserTemplates.IsUser(template)) return;
        if (MessageBox.Show(this, $"Delete the template \"{template.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes) return;
        try
        {
            UserTemplates.Delete(_userFolder, template.Name);
            _list.Items.Remove(_list.SelectedItems[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not delete the template:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
