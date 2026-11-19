namespace ReportStudio.Web;

/// <summary>
/// The settings of the web viewer (requirement 49), read from the "ReportStudio" section of appsettings.json, environment variables
/// (<c>ReportStudio__ReportsFolder</c>) or the command line (<c>--ReportStudio:ReportsFolder=D:\reports</c>).
/// Nothing here is chosen by a visitor: report files are somebody else's input, so what they may read is decided by whoever runs the server.
/// </summary>
public sealed class WebViewerOptions
{
    /// <summary>The folder whose .rsr files are offered (not its sub-folders). A relative path is taken from the program's working folder.</summary>
    public string ReportsFolder { get; set; } = "reports";

    /// <summary>Reports may read JSON, XML, CSV and Excel files (relative to the report's folder, and inside <see cref="DataRoot"/> when that is set).</summary>
    public bool AllowDataFiles { get; set; }

    /// <summary>Reports may read REST and OData web addresses.</summary>
    public bool AllowWebData { get; set; }

    /// <summary>
    /// Reports that carry their own database query may run. Off by default: nobody is there to read the query and say yes, so the owner of
    /// the server says it once for the whole folder. Turn it on only when the folder is written by people you trust.
    /// </summary>
    public bool TrustReportQueries { get; set; }

    /// <summary>When set, data files can only be read from inside this folder.</summary>
    public string? DataRoot { get; set; }

    /// <summary>The culture for numbers and dates. The default en-IN is ReportStudio's own Indian number grouping.</summary>
    public string Culture { get; set; } = "en-IN";

    /// <summary>How many reports may be worked out at the same time; the others wait.</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>How long a request waits for a free place before it is answered "busy" (seconds).</summary>
    public int QueueSeconds { get; set; } = 30;

    /// <summary>The biggest report file that is opened (kilobytes).</summary>
    public int MaxReportFileKb { get; set; } = 4096;
}
