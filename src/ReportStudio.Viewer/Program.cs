using ReportStudio.Core.Model;
using ReportStudio.UI;

namespace ReportStudio.Viewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ReportStudio.Data.DatabaseLoader.Register();
        ApplicationConfiguration.Initialize();
        Application.Run(new ViewerForm(args.FirstOrDefault(a => File.Exists(a))));
    }
}

/// <summary>Stand-alone viewer for distributing reports: open a .rsr file, enter parameters, view / export / print.</summary>
internal sealed class ViewerForm : Form
{
    private readonly ViewerControl _viewer = new();
    private ReportDefinition? _definition;
    private string? _path;
    private Dictionary<string, string?> _lastParameters = new();

    public ViewerForm(string? initialFile)
    {
        Text = "ReportStudio Viewer";
        Width = 1000;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        var open = new ToolStripMenuItem("&Open report…", null, (_, _) => OpenWithDialog()) { ShortcutKeys = Keys.Control | Keys.O };
        var refresh = new ToolStripMenuItem("&Refresh data…", null, (_, _) => RunReport(prompt: true)) { ShortcutKeys = Keys.F5 };
        var exit = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        file.DropDownItems.AddRange(new ToolStripItem[] { open, refresh, new ToolStripSeparator(), exit });
        menu.Items.Add(file);

        MainMenuStrip = menu;
        Controls.Add(_viewer);
        Controls.Add(menu);

        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenFile(files[0]);
        };

        if (initialFile != null) Shown += (_, _) => OpenFile(initialFile);
    }

    private void OpenWithDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "ReportStudio report (*.rsr)|*.rsr|All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        try
        {
            var loaded = ReportDefinition.Load(path);
            if (!UntrustedFileGuard.Confirm(this, loaded, path)) return;
            _definition = loaded;
            _path = path;
            _lastParameters = new Dictionary<string, string?>();
            Text = $"{_definition.Name} - ReportStudio Viewer";
            RunReport(prompt: true);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this, "Could not open the report:\n" + ex.Message, "Open report",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunReport(bool prompt)
    {
        if (_definition == null) return;
        if (prompt && _definition.Parameters.Count > 0)
        {
            using var dlg = new ParameterDialog(_definition.Parameters, _lastParameters);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _lastParameters = dlg.Values;
        }
        _viewer.Run(_definition, _lastParameters, Path.GetDirectoryName(_path));
    }
}
