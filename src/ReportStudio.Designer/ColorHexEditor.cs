using System.ComponentModel;
using System.Drawing.Design;
using ReportStudio.Export;

namespace ReportStudio.Designer;

/// <summary>
/// PropertyGrid editor for colour properties that are stored as hex strings ("#RRGGBB").
/// Referenced by name from ReportItem so the Core library stays free of UI dependencies.
/// </summary>
public sealed class ColorHexEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;

    public override bool GetPaintValueSupported(ITypeDescriptorContext? context) => true;

    public override void PaintValue(PaintValueEventArgs e)
    {
        var color = ColorUtil.Parse(e.Value as string);
        if (color == null) return;
        using var brush = new SolidBrush(color.Value);
        e.Graphics.FillRectangle(brush, e.Bounds);
    }

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        using var dlg = new ColorDialog { FullOpen = true };
        var current = ColorUtil.Parse(value as string);
        if (current != null) dlg.Color = current.Value;

        if (dlg.ShowDialog() != DialogResult.OK) return value;
        var c = dlg.Color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
