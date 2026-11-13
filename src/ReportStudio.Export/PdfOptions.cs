namespace ReportStudio.Export;

/// <summary>The archive standard a PDF follows.</summary>
public enum PdfConformance
{
    /// <summary>An ordinary PDF.</summary>
    None,
    /// <summary>PDF/A-2b: a file meant to look the same in decades.</summary>
    PdfA2b
}

/// <summary>
/// Protection for a PDF (for example payslips sent by e-mail). Leave everything at its default for an ordinary PDF.
/// The PDF format limits passwords to Latin-1 characters (letters, digits and common punctuation are safe).
/// </summary>
public sealed class PdfOptions
{
    /// <summary>Needed to open the file. Empty = anyone can open it.</summary>
    public string? UserPassword { get; set; }

    /// <summary>
    /// Needed to change the restrictions below. Empty = a random one is generated and thrown away,
    /// so nobody can lift the restrictions afterwards.
    /// </summary>
    public string? OwnerPassword { get; set; }

    public bool AllowPrint { get; set; } = true;

    /// <summary>Copy text and graphics out of the document.</summary>
    public bool AllowCopy { get; set; } = true;

    /// <summary>Edit the document, add annotations, reassemble pages.</summary>
    public bool AllowModify { get; set; } = true;

    /// <summary>
    /// PdfA2b makes an archive copy (ISO 19005-2, level B): fonts embedded, colours declared as sRGB, metadata that matches the
    /// document information. It cannot be combined with passwords or restrictions, because PDF/A forbids encryption.
    /// </summary>
    public PdfConformance Conformance { get; set; } = PdfConformance.None;

    /// <summary>
    /// Writes a tagged PDF: text becomes paragraphs and pictures and codes figures in the reading order of the page, and
    /// backgrounds, lines and boxes are marked as page decoration. Screen readers and "reflow" views use this.
    /// </summary>
    public bool Tagged { get; set; }

    /// <summary>The language of the text for a tagged PDF, for example "en-IN". Empty = "en".</summary>
    public string? Language { get; set; }

    /// <summary>The creation date written into a PDF/A file. Empty = now. Set it to get the same bytes on every run.</summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>True when the file has to be encrypted: a password is set or something is not allowed.</summary>
    public bool NeedsEncryption =>
        !string.IsNullOrEmpty(UserPassword) || !string.IsNullOrEmpty(OwnerPassword) ||
        !AllowPrint || !AllowCopy || !AllowModify;
}
