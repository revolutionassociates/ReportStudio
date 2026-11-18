using System.Text.Json.Serialization;

namespace ReportStudio.Core.Model;

/// <summary>
/// A named look (requirement 46) that elements share: an element whose <see cref="ReportItem.Style"/> names it takes every value set here
/// instead of its own, so changing the style changes all of them at once. A value left empty (null) is not part of the style and the
/// element keeps its own; for the colours, "" is a value of its own and means "no colour". A style can be based on another one
/// (<see cref="Parent"/>) and then only sets what differs.
/// </summary>
public class StyleDefinition
{
    public string Name { get; set; } = "";

    /// <summary>The style this one is based on: it starts with the values of that style and sets only what it changes. Empty = none.</summary>
    public string Parent { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FontFamily { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FontSize { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Bold { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Italic { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Underline { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HAlign? HAlign { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public VAlign? VAlign { get; set; }

    /// <summary>Text (and line) colour, hex such as #1F2937.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ForeColor { get; set; }

    /// <summary>Fill colour, hex. "" = no fill.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BackColor { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BorderColor { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? BorderWidth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? CornerRadius { get; set; }

    /// <summary>True when the style sets nothing of its own (it may still be based on another one).</summary>
    [JsonIgnore]
    public bool SetsNothing =>
        FontFamily == null && FontSize == null && Bold == null && Italic == null && Underline == null && HAlign == null && VAlign == null &&
        ForeColor == null && BackColor == null && BorderColor == null && BorderWidth == null && CornerRadius == null;

    public StyleDefinition Clone() => (StyleDefinition)MemberwiseClone();

    public override string ToString() => Name;
}
