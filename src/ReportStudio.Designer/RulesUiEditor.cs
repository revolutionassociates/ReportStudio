using System.ComponentModel;
using System.Drawing.Design;
using ReportStudio.Core.Model;

namespace ReportStudio.Designer;

/// <summary>
/// PropertyGrid editor that opens the conditional formatting editor for an element's rules. Referenced by name from
/// ReportItem so the Core library stays free of UI dependencies.
/// </summary>
public sealed class RulesUiEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        // With several elements selected the grid would give each of them the same list object.
        if (context?.Instance is object[] many && many.Length > 1)
        {
            MessageBox.Show("Select one element at a time to edit its conditional formatting.", "Conditional formatting");
            return value;
        }

        using var dlg = new RulesEditorDialog(value as IEnumerable<FormatRule> ?? Array.Empty<FormatRule>());
        return dlg.ShowDialog() == DialogResult.OK ? dlg.Rules : value;
    }
}
