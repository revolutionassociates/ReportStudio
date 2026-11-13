namespace ReportStudio.Core.Data.Sources;

/// <summary>
/// What a run may read from outside the report (requirement 40). A host that only shows its own reports turns file and web sources
/// off, so a report file cannot make the program read anything but the rows the host supplies. The viewer has both off unless asked.
/// </summary>
public sealed class SourceAccess
{
    /// <summary>JSON, XML, CSV and Excel files may be read.</summary>
    public bool AllowFileSources { get; init; } = true;

    /// <summary>REST and OData web addresses may be read.</summary>
    public bool AllowWebSources { get; init; } = true;

    /// <summary>
    /// Web addresses may point at this computer or the local network (10.x, 192.168.x, localhost, ...) and use plain http.
    /// Off by default: a report written by someone else could otherwise probe the network the viewer runs in.
    /// </summary>
    public bool AllowPrivateAddresses { get; init; }

    /// <summary>How long a web request may take in all. Default 30 seconds.</summary>
    public TimeSpan WebTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Replaces the real network connection (for tests, or a host with its own HTTP stack). The address checks still apply to the address text.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    public const string FilesOffMessage =
        "This program does not let reports read data files. Ask whoever sets up the program to allow data files, or use a report whose data comes from the application.";

    public const string WebOffMessage =
        "This program does not let reports read web addresses. Ask whoever sets up the program to allow web data, or use a report whose data comes from the application.";
}
