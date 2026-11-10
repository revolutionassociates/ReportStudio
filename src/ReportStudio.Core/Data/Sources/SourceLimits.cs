namespace ReportStudio.Core.Data.Sources;

/// <summary>The most a file or web source is allowed to be, so a crafted or runaway source cannot use up the machine's memory.</summary>
public static class SourceLimits
{
    /// <summary>Largest file or download read, in bytes.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Most rows read from one source.</summary>
    public const int MaxRows = 1_000_000;

    /// <summary>Most fields (columns) in one row.</summary>
    public const int MaxColumns = 1000;

    /// <summary>Deepest nesting of objects or elements read.</summary>
    public const int MaxDepth = 64;

    /// <summary>Largest an .xlsx part may be once unpacked (a small zip can unpack to a huge file).</summary>
    public const long MaxUnpackedBytes = 200L * 1024 * 1024;

    /// <summary>Longest text value kept; longer values are cut.</summary>
    public const int MaxValueChars = 100_000;
}

/// <summary>The source is not usable: too big, not what it says it is, outside the allowed folder, and so on. The message is safe to show.</summary>
public sealed class SourceException : Exception
{
    public SourceException(string message) : base(message) { }
    public SourceException(string message, Exception inner) : base(message, inner) { }
}
