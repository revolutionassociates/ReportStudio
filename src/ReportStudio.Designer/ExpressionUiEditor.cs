using System.ComponentModel;
using System.Drawing.Design;

namespace ReportStudio.Designer;

/// <summary>
/// PropertyGrid editor that opens the expression editor for a property holding one expression (for example
/// "Visible when"). Referenced by name from ReportItem so the Core library stays free of UI dependencies.
/// </summary>
public sealed class ExpressionUiEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        var title = context?.PropertyDescriptor?.DisplayName ?? "Expression";
        using var dlg = new ExpressionEditorDialog(title, value as string ?? "");
        return dlg.ShowDialog() == DialogResult.OK ? dlg.Expression : value;
    }
}
