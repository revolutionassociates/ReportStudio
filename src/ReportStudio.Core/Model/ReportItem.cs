using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ReportStudio.Core.Model;

public enum ItemKind { Text, Line, Rectangle, Image, Barcode, QrCode, SubReport, CrossTab }
public enum CrossAggregate { Sum, Count, Average, Min, Max }
public enum HAlign { Left, Center, Right }
public enum VAlign { Top, Middle, Bottom }

/// <summary>
/// One design element placed on a band. A single class (with a Kind) keeps the JSON format simple
/// and lets the designer's PropertyGrid edit every element type the same way.
/// </summary>
public class ReportItem
{
    private const string ColorEditor =
        "ReportStudio.Designer.ColorHexEditor, ReportStudio.Designer";
    private const string EditorBase =
        "System.Drawing.Design.UITypeEditor, System.Drawing";

    [Category("Design"), Description("Internal name of the element.")]
    public string Name { get; set; } = "";

    [Category("Design")]
    public ItemKind Kind { get; set; } = ItemKind.Text;

    // ---- Layout (pixels @96dpi, relative to the band's top-left inside the page margins) ----
    [Category("Layout")] public double X { get; set; }
    [Category("Layout")] public double Y { get; set; }
    [Category("Layout")] public double Width { get; set; } = 100;
    [Category("Layout")] public double Height { get; set; } = 20;

    // ---- Content ----
    [Category("Content"),
     Description("Literal text with {expressions}, e.g. 'Total: {Sum(Amount)}' or just '{Customer}'. " +
                 "Fields can be written as Name or [Name]. Special values: Page, PageCount, Now, @Parameter.")]
    public string Text { get; set; } = "";

    [Category("Content"),
     Description("Format applied to {expression} values: e.g. N2, C, #,##0.00, dd-MMM-yyyy.")]
    public string Format { get; set; } = "";

    [Category("Content"), Description("When true the element grows downward to fit wrapped text.")]
    public bool CanGrow { get; set; }

    [Category("Content"), Description("Optional expression; the element is printed only when it evaluates to true.")]
    public string VisibleWhen { get; set; } = "";

    // ---- Appearance ----
    /// <summary>The font new items use. Ubuntu ships with the application, so reports look the same everywhere.</summary>
    public const string DefaultFontFamily = "Ubuntu";

    [Category("Appearance")] public string FontFamily { get; set; } = DefaultFontFamily;
    [Category("Appearance")] public double FontSize { get; set; } = 9;
    [Category("Appearance")] public bool Bold { get; set; }
    [Category("Appearance")] public bool Italic { get; set; }
    [Category("Appearance")] public bool Underline { get; set; }
    [Category("Appearance")] public HAlign HAlign { get; set; } = HAlign.Left;
    [Category("Appearance")] public VAlign VAlign { get; set; } = VAlign.Top;
    [Category("Appearance")] public bool WordWrap { get; set; } = true;

    [Category("Appearance"), Description("Hex colour such as #1F2937. Empty = transparent."),
     Editor(ColorEditor, EditorBase)]
    public string ForeColor { get; set; } = "#000000";

    [Category("Appearance"), Description("Hex colour fill. Empty = transparent."),
     Editor(ColorEditor, EditorBase)]
    public string BackColor { get; set; } = "";

    [Category("Appearance"), Editor(ColorEditor, EditorBase)]
    public string BorderColor { get; set; } = "";

    [Category("Appearance")] public double BorderWidth { get; set; }

    // ---- Barcode (Kind = Barcode; the value to encode is Text, e.g. "{EmpCode}") ----
    [Category("Barcode")] public ReportStudio.Core.Barcodes.BarcodeType BarcodeType { get; set; }

    [Category("Barcode"), Description("Print the value under the bars, in this element's font.")]
    public bool ShowText { get; set; } = true;

    // ---- QR code (Kind = QrCode; the value to encode is Text, e.g. "upi://pay?pa={Upi}&am={Net}") ----
    [Category("QR code"), Description("How much damage the code survives: Low 7%, Medium 15%, Quartile 25%, High 30%. Higher levels make a denser code.")]
    public ReportStudio.Core.Barcodes.QrLevel QrLevel { get; set; } = ReportStudio.Core.Barcodes.QrLevel.Medium;

    // ---- Sub-report (Kind = SubReport): another report drawn inside this band, as wide as this element ----
    [Category("Sub-report"), Description("The sub-report: a name the host registered, or a .rsr file next to this report (never outside its folder).")]
    public string SubReportPath { get; set; } = "";

    [Category("Sub-report"), Description("Where its rows come from: a field of this row that holds a list of rows, or the name of a data set the host supplied.")]
    public string SubReportData { get; set; } = "";

    [Category("Sub-report"), Description("When the data is a data set: the sub-report's field that must equal the value below, e.g. EmpCode.")]
    public string LinkChild { get; set; } = "";

    [Category("Sub-report"), Description("When the data is a data set: this row's value to match against the field above, e.g. EmpCode. Empty = same as the field.")]
    public string LinkParent { get; set; } = "";

    [Category("Sub-report"), Description("Values for the sub-report's parameters, separated by semicolons: Month={@PayMonth}; Code={EmpCode}")]
    public string SubReportParameters { get; set; } = "";

    // ---- Cross-tab (Kind = CrossTab): rows x columns summary of the rows in this band's scope ----
    [Category("Cross-tab"), Description("Expression whose distinct values become the rows, e.g. Department.")]
    public string CrossRows { get; set; } = "";

    [Category("Cross-tab"), Description("Expression whose distinct values become the columns, e.g. Month.")]
    public string CrossColumns { get; set; } = "";

    [Category("Cross-tab"), Description("Expression summarised in each cell, e.g. Net. Not needed for Count.")]
    public string CrossValue { get; set; } = "";

    [Category("Cross-tab")] public CrossAggregate CrossAggregate { get; set; } = CrossAggregate.Sum;

    [Category("Cross-tab"), Description("Text in the top-left corner cell.")]
    public string CrossCorner { get; set; } = "";

    [Category("Cross-tab"), Description("Width of the row-label column, in pixels.")]
    public double CrossLabelWidth { get; set; } = 100;

    [Category("Cross-tab"), Description("Height of every row, in pixels.")]
    public double CrossRowHeight { get; set; } = 20;

    [Category("Cross-tab"), Description("Add a Total column (and a Total row below).")]
    public bool CrossTotals { get; set; } = true;

    // ---- Image ----
    [Browsable(false)]
    public string ImageBase64 { get; set; } = "";

    [Category("Image"), Description("Optional local file path, or a {expression} that yields one, loaded at run time.")]
    public string ImagePath { get; set; } = "";

    [JsonIgnore, Browsable(false)] public double Right => X + Width;
    [JsonIgnore, Browsable(false)] public double Bottom => Y + Height;

    public ReportItem Clone() => (ReportItem)MemberwiseClone();

    public override string ToString() => string.IsNullOrEmpty(Name) ? Kind.ToString() : Name;
}
