using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ReportStudio.Core.Model;

public enum ItemKind { Text, Line, Rectangle, Image, Barcode, QrCode, SubReport, CrossTab, Contents, Barcode2D, Ellipse }
/// <summary>Which way a gradient fill changes colour: from the back colour at the start to the gradient colour at the end.</summary>
public enum GradientDirection { TopToBottom, LeftToRight }
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
    private const string ExpressionEditor =
        "ReportStudio.Designer.ExpressionUiEditor, ReportStudio.Designer";
    private const string RulesEditor =
        "ReportStudio.Designer.RulesUiEditor, ReportStudio.Designer";
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

    [Category("Layout"), Description("A locked element cannot be moved, resized or deleted in the designer. Untick to change it again.")]
    public bool Locked { get; set; }

    /// <summary>Elements with the same non-empty value were grouped in the designer: they are selected and moved together.</summary>
    [Browsable(false)]
    public string ItemGroup { get; set; } = "";

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

    [Category("Content"), Description("Do not print the element when its text is empty (no box, fill or border). The space stays; use Can shrink to close it.")]
    public bool SuppressIfBlank { get; set; }

    [Category("Content"), Description("When the element is empty (no text or no picture) it gives up its height, and elements below it " +
                                      "that overlap it sideways move up. An element with content keeps its designed height.")]
    public bool CanShrink { get; set; }

    [Category("Content"), Description("Optional expression; the element is printed only when it evaluates to true."),
     Editor(ExpressionEditor, EditorBase)]
    public string VisibleWhen { get; set; } = "";

    [Category("Interaction"), Description("Makes the element clickable in the PDF, the viewer and HTML. Write an address with {expressions}: " +
                                          "https://example.com/emp/{EmpCode}, mailto:{Email}, tel:{Phone}, or #Name to jump to a bookmark in this report.")]
    public string Link { get; set; } = "";

    [Category("Interaction"), Description("Names this place in the report so a link (#Name) or a table of contents can jump to it. May use {expressions}, e.g. Employee-{EmpCode}.")]
    public string Bookmark { get; set; } = "";

    [Category("Interaction"), Description("Drill-through: clicking the element in the viewer opens this report (a .rsr file in the same folder as this report) in a new window. May use {expressions}.")]
    public string DrillReport { get; set; } = "";

    [Category("Interaction"), Description("Drill-through: values for the opened report's parameters, separated by semicolons: Month={@PayMonth}; Code={EmpCode}")]
    public string DrillParameters { get; set; } = "";

    [Category("Interaction"), Description("Puts this place in the PDF's bookmark panel and in a table of contents. Write the title, with {expressions}: " +
                                          "e.g. Employee {Name}. Empty = not listed.")]
    public string Outline { get; set; } = "";

    [Category("Interaction"), Description("How deep the title sits: 1 is a main heading, 2 sits under the last 1, and so on.")]
    public int OutlineLevel { get; set; } = 1;

    // ---- Table of contents (Kind = Contents): lists every element that has an Outline title, with its page number ----
    [Category("Contents"), Description("Deepest level listed (1 = main headings only).")]
    public int ContentsLevels { get; set; } = 3;

    [Category("Contents"), Description("Height of each line, in pixels. The box keeps its designed height, so lines that do not fit are left out with a warning.")]
    public double ContentsRowHeight { get; set; } = 20;

    [Category("Contents"), Description("How far each level is moved in, in pixels.")]
    public double ContentsIndent { get; set; } = 16;

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

    [Category("Appearance"), Description("Second colour of a gradient fill (hex). The fill then runs from Back colour to this colour. " +
                                          "Empty = solid fill. Works on boxes, ellipses and text boxes; needs a Back colour."),
     Editor(ColorEditor, EditorBase)]
    public string GradientColor { get; set; } = "";

    [Category("Appearance"), Description("Which way the gradient runs: top to bottom, or left to right.")]
    public GradientDirection GradientDirection { get; set; }

    [Category("Appearance"), Editor(ColorEditor, EditorBase)]
    public string BorderColor { get; set; } = "";

    [Category("Appearance")] public double BorderWidth { get; set; }

    [Category("Appearance"), Description("Rounds the corners of a box (Kind = Rectangle), in pixels. 0 = square corners. A value larger than half the shorter side makes the ends fully round.")]
    public double CornerRadius { get; set; }

    [Category("Appearance"), Description("Turns the text (not the box, its fill or its border) counter-clockwise by this many degrees about the middle of the box: " +
                                          "90 reads from bottom to top, 270 from top to bottom, 180 is upside down. Near a quarter turn the text wraps along the box's height, " +
                                          "so make the box tall and narrow for a vertical column heading. A turned text does not grow with Can grow. 0 = upright.")]
    public double TextRotation { get; set; }

    // ---- Barcode (Kind = Barcode; the value to encode is Text, e.g. "{EmpCode}") ----
    [Category("Barcode")] public ReportStudio.Core.Barcodes.BarcodeType BarcodeType { get; set; }

    [Category("Barcode"), Description("Print the value under the bars, in this element's font.")]
    public bool ShowText { get; set; } = true;

    // ---- QR code (Kind = QrCode; the value to encode is Text, e.g. "upi://pay?pa={Upi}&am={Net}") ----
    [Category("QR code"), Description("How much damage the code survives: Low 7%, Medium 15%, Quartile 25%, High 30%. Higher levels make a denser code.")]
    public ReportStudio.Core.Barcodes.QrLevel QrLevel { get; set; } = ReportStudio.Core.Barcodes.QrLevel.Medium;

    // ---- Two-dimensional code (Kind = Barcode2D; the value to encode is Text, e.g. "{PartNo}") ----
    [Category("2D code"), Description("Which code: Data Matrix (small and square), PDF417 (wide, for long values; make the box wider than tall) or Aztec (square, read from any angle). The value to encode is the Text, e.g. {PartNo}. An unreadable or too long value prints nothing and gives a warning.")]
    public ReportStudio.Core.Barcodes.Barcode2DType Barcode2DType { get; set; }

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

    [Category("Image"), Description("Where the picture comes from at run time: a local file path (or a text with {expressions} that gives one), " +
                                    "or a single {Field} that holds the picture itself: a database image column, base64 text, or a data:image/png;base64,... address. " +
                                    "An empty field prints no picture. PNG, JPEG, GIF, BMP, TIFF, and plain SVG drawings (no scripts or links to other files or web addresses).")]
    public string ImagePath { get; set; } = "";

    [JsonIgnore, Browsable(false)] public double Right => X + Width;
    [JsonIgnore, Browsable(false)] public double Bottom => Y + Height;

    /// <summary>Conditional formatting: each rule whose condition is true changes colours and font style, in order.</summary>
    [Category("Appearance"), DisplayName("Conditional formats"),
     Description("Rules that change colours and font style when a condition is true, e.g. red text when Net < 0. Click ... to edit."),
     Editor(RulesEditor, EditorBase)]
    public List<FormatRule> Rules { get; set; } = new();

    public ReportItem Clone()
    {
        var copy = (ReportItem)MemberwiseClone();
        copy.Rules = Rules.Select(r => r.Clone()).ToList();     // a copy must not share rules with the original
        return copy;
    }

    public override string ToString() => string.IsNullOrEmpty(Name) ? Kind.ToString() : Name;
}
