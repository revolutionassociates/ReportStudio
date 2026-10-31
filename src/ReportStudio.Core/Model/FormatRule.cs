namespace ReportStudio.Core.Model;

/// <summary>
/// Changes how an element looks when a condition is true, e.g. red text for a negative balance or a shaded row
/// for an overdue invoice. Every rule of an element is checked in order and each one that is true applies, so a
/// later rule wins where two change the same setting. Settings left empty are not touched.
/// </summary>
public class FormatRule
{
    /// <summary>Expression that must be true, e.g. "Net &lt; 0" or "Status == 'Overdue'". Fields, variables and parameters can be used.</summary>
    public string Condition { get; set; } = "";

    /// <summary>Hex colour for the text (and line, barcode or QR code). Empty = unchanged.</summary>
    public string ForeColor { get; set; } = "";

    /// <summary>Hex fill colour. Empty = unchanged.</summary>
    public string BackColor { get; set; } = "";

    /// <summary>Hex border colour. Empty = unchanged.</summary>
    public string BorderColor { get; set; } = "";

    /// <summary>true or false to force it; null = unchanged.</summary>
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }

    public FormatRule Clone() => (FormatRule)MemberwiseClone();
}
