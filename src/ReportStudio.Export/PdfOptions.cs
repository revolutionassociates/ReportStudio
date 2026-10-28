namespace ReportStudio.Export;

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

    /// <summary>True when the file has to be encrypted: a password is set or something is not allowed.</summary>
    public bool NeedsEncryption =>
        !string.IsNullOrEmpty(UserPassword) || !string.IsNullOrEmpty(OwnerPassword) ||
        !AllowPrint || !AllowCopy || !AllowModify;
}
