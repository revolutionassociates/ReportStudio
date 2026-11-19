using System.ComponentModel;
using System.Drawing.Design;
using System.Windows.Forms.Design;

namespace ReportStudio.Designer;

/// <summary>The style names of the open report, for the Style list of the property grid. The main window keeps it current.</summary>
internal static class StyleEditorContext
{
    public static Func<IReadOnlyList<string>> Names { get; set; } = () => Array.Empty<string>();
}

/// <summary>
/// PropertyGrid editor for an element's Style: a drop-down list of the report's styles, with "(none)" to take the style off.
/// Referenced by name from ReportItem so the Core library stays free of UI dependencies.
/// </summary>
public sealed class StyleUiEditor : UITypeEditor
{
    private const string None = "(none)";

    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.DropDown;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        if (provider.GetService(typeof(IWindowsFormsEditorService)) is not IWindowsFormsEditorService service) return value;

        var names = StyleEditorContext.Names();
        var current = (value as string)?.Trim() ?? "";

        var list = new ListBox { BorderStyle = BorderStyle.None, IntegralHeight = false };
        list.Items.Add(None);
        foreach (var name in names) list.Items.Add(name);
        // A name the report does not have is kept in the list, so opening the list and closing it does not lose it.
        if (current.Length > 0 && !names.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Items.Add(current);

        int at = current.Length == 0 ? 0 : list.Items.IndexOf(names.FirstOrDefault(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase)) ?? current);
        list.SelectedIndex = Math.Max(0, at);
        list.Height = Math.Min(220, Math.Max(40, list.Items.Count * list.ItemHeight + 4));

        string? chosen = null;
        list.Click += (_, _) => { chosen = list.SelectedItem as string; service.CloseDropDown(); };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            chosen = list.SelectedItem as string;
            service.CloseDropDown();
        };

        service.DropDownControl(list);
        if (chosen == null) return value;
        return chosen == None ? "" : chosen;
    }
}
