using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Formatting;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Web;

public enum RunStatus { Ok, NotFound, BadRequest, Refused, Busy, Failed }

/// <summary>The answer to "open this report" or "run this report": a status, a sentence for the visitor, and (when it worked) the report or its result.</summary>
public sealed record ReportView(RunStatus Status, string Message, ReportDefinition? Definition = null, ReportResult? Result = null)
{
    public int HttpStatus => Status switch
    {
        RunStatus.Ok => 200,
        RunStatus.NotFound => 404,
        RunStatus.BadRequest => 400,
        RunStatus.Refused => 403,
        RunStatus.Busy => 503,
        _ => 500
    };
}

/// <summary>A report written in one download format: its bytes, or null and a sentence for the visitor when it could not be written.</summary>
public sealed record ReportFile(byte[]? Bytes, string Message);

/// <summary>
/// Opens and runs the reports of the web viewer (requirement 49). It applies the same trust rules as the command-line runner, decided by the
/// server's settings and never by the visitor, and keeps the number of reports being worked out at one time within <see cref="WebViewerOptions.MaxConcurrentRuns"/>.
/// What a visitor is told never includes a file path, a query or a connection string; the full error goes to the server log.
/// </summary>
public sealed class ReportService : IDisposable
{
    public const int MaxParameterChars = 1000;

    private readonly ReportCatalog _catalog;
    private readonly IOptions<WebViewerOptions> _options;
    private readonly WebTextMeasurer _measurer;
    private readonly ILogger<ReportService> _log;
    private readonly SemaphoreSlim _slots;
    private readonly CultureInfo _culture;

    public ReportService(ReportCatalog catalog, IOptions<WebViewerOptions> options, WebTextMeasurer measurer, ILogger<ReportService> log)
    {
        _catalog = catalog;
        _options = options;
        _measurer = measurer;
        _log = log;
        _slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentRuns));
        _culture = ReadCulture(options.Value.Culture);
    }

    public void Dispose() => _slots.Dispose();

    private static CultureInfo ReadCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), "en-IN", StringComparison.OrdinalIgnoreCase)) return IndianFormat.Culture;
        try { return CultureInfo.GetCultureInfo(name.Trim()); }
        catch (CultureNotFoundException) { return IndianFormat.Culture; }
    }

    /// <summary>Why this server will not run the report, or null when it will. The wording is for the visitor, so it names the setting, not the query.</summary>
    public string? Refusal(ReportDefinition def)
    {
        var o = _options.Value;
        if (def.DataSource.Provider == DbProvider.Host)
            return "This report takes its data from a host application, so it cannot be shown here.";
        if (def.UsesFileSources && !o.AllowDataFiles)
            return "This server does not let reports read data files (setting ReportStudio:AllowDataFiles).";
        if (def.UsesWebSources && !o.AllowWebData)
            return "This server does not let reports read web addresses (setting ReportStudio:AllowWebData).";
        if (TrustCheck.NeedsDatabaseTrust(def) && !o.TrustReportQueries)
            return "This report carries its own database query, and this server is not set to trust the report files (setting ReportStudio:TrustReportQueries).";
        return null;
    }

    /// <summary>Opens a report to show its parameter form: found, readable and allowed to run.</summary>
    public ReportView Open(string? id) => OpenWithPath(id).View;

    private (ReportView View, string? Path) OpenWithPath(string? id)
    {
        var path = _catalog.Resolve(id);
        if (path == null) return (new ReportView(RunStatus.NotFound, "There is no such report."), null);

        ReportDefinition def;
        try
        {
            def = _catalog.Load(path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogWarning(ex, "The report file {Report} could not be read", id);
            return (new ReportView(RunStatus.Failed, "The report file could not be read."), null);
        }

        var refusal = Refusal(def);
        if (refusal != null)
        {
            _log.LogWarning("Report {Report} refused: {Reason}. It would read: {Sources}", id, refusal, string.Join(" | ", TrustCheck.Describe(def, 300)));
            return (new ReportView(RunStatus.Refused, refusal), null);
        }
        return (new ReportView(RunStatus.Ok, "", def), path);
    }

    /// <summary>
    /// Works out a report. <paramref name="raw"/> holds what the visitor typed, by parameter name (several values for a multi-value parameter).
    /// A name the report does not have, or a value that is not a number, date or yes/no where one is needed, is a bad request.
    /// </summary>
    public async Task<ReportView> RunAsync(string? id, IReadOnlyDictionary<string, string[]> raw, CancellationToken ct)
    {
        var (opened, path) = OpenWithPath(id);
        if (opened.Status != RunStatus.Ok || opened.Definition == null || path == null) return opened;
        var def = opened.Definition;

        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in raw)
        {
            var known = def.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known == null) return new ReportView(RunStatus.BadRequest, "The report has no parameter named '" + Shorten(name) + "'.");
            if (values.Any(v => v.Length > MaxParameterChars)) return new ReportView(RunStatus.BadRequest, $"The value of '{Label(known)}' is too long.");

            parameters[known.Name] = values.Length == 0 ? null
                : known.MultiValue ? ValueList.Join(values)
                : values[^1];
        }

        foreach (var p in def.Parameters)
        {
            try
            {
                p.Parse(parameters.GetValueOrDefault(p.Name));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return new ReportView(RunStatus.BadRequest, $"The value of '{Label(p)}' is not valid.");
            }
        }

        var wait = TimeSpan.FromSeconds(Math.Max(1, _options.Value.QueueSeconds));
        if (!await _slots.WaitAsync(wait, ct).ConfigureAwait(false))
            return new ReportView(RunStatus.Busy, "The server is busy working out other reports. Please try again in a moment.");

        try
        {
            var result = await Task.Run(() => Execute(def, path, parameters), ct).ConfigureAwait(false);
            return new ReportView(RunStatus.Ok, "", def, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _log.LogError(ex, "Report {Report} failed", id);
            return new ReportView(RunStatus.Failed, "The report could not be worked out: " + Shorten(ex.Message));
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>The culture numbers and dates are written in.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Writes a worked-out report as a download. A failure is logged in full and told to the visitor in one sentence.</summary>
    public ReportFile Render(DownloadFormat format, ReportResult result, string? id)
    {
        try
        {
            return new ReportFile(ReportDownloads.Write(format, result, _culture), "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Report {Report} could not be written as {Format}", id, format.Id);
            return new ReportFile(null, "The " + format.Label + " could not be created.");
        }
    }

    private ReportResult Execute(ReportDefinition def, string path, Dictionary<string, string?> parameters)
    {
        var o = _options.Value;
        var options = new ReportOptions
        {
            Culture = _culture,
            BaseDirectory = Path.GetDirectoryName(path),
            Measurer = _measurer,
            SourceRoot = string.IsNullOrWhiteSpace(o.DataRoot) ? null : Path.GetFullPath(o.DataRoot),
            Access = new SourceAccess { AllowFileSources = o.AllowDataFiles, AllowWebSources = o.AllowWebData }
        };
        return ReportEngine.Run(def, parameters, options);
    }

    private static string Label(ReportParameter p) => string.IsNullOrWhiteSpace(p.Prompt) ? p.Name : p.Prompt;

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
