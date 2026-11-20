using System.Globalization;
using Microsoft.Extensions.Options;
using ReportStudio.Core.Model;
using ReportStudio.Core.Server;

namespace ReportStudio.Web;

/// <summary>What happened when a subscription was run: sent, or one sentence saying why not (kept in the subscription file, so it holds no path or query).</summary>
public sealed record SubscriptionOutcome(bool Ok, string Message);

/// <summary>
/// Runs one subscription (requirement 50): checks it against this server's settings, works out the report with the same rules as the web
/// viewer, writes the attachment and mails it. Every check is made again here, at run time, because the file may have been edited by hand
/// and the settings may have changed since the subscription was stored.
/// </summary>
public sealed class SubscriptionRunner
{
    private readonly ReportService _reports;
    private readonly IOptions<ServerOptions> _options;
    private readonly IMailSender _mail;
    private readonly TimeProvider _time;
    private readonly ILogger<SubscriptionRunner> _log;

    public SubscriptionRunner(ReportService reports, IOptions<ServerOptions> options, IMailSender mail, TimeProvider time, ILogger<SubscriptionRunner> log)
    {
        _reports = reports;
        _options = options;
        _mail = mail;
        _time = time;
        _log = log;
    }

    /// <summary>Why the server would not send this subscription (without running it), or null. Used by the admin API too.</summary>
    public string? Refusal(Subscription s)
    {
        var o = _options.Value;
        var problems = s.Problems();
        if (problems.Count > 0) return problems[0];
        if (!ReportCatalog.IsValidId(s.ReportId)) return "The report name is not allowed.";
        if (ReportDownloads.Find(s.Format) == null)
            return "The format '" + s.Format + "' is not offered; use one of " + string.Join(", ", ReportDownloads.Formats.Select(f => f.Id)) + ".";
        if (s.Recipients.Count > Subscription.MaxRecipients) return "There are too many recipients.";
        foreach (var r in s.Recipients)
            if (!EmailAddress.IsAllowed(r, o.AllowedRecipientDomains))
                return "This server may not send reports to '" + (r.Length <= 60 ? r : r[..60] + "...") + "' (setting ReportStudio:Server:AllowedRecipientDomains).";
        return null;
    }

    public async Task<SubscriptionOutcome> RunAsync(Subscription s, CancellationToken ct)
    {
        var o = _options.Value;
        if (!o.Enabled) return new SubscriptionOutcome(false, "The report server is switched off (setting ReportStudio:Server:Enabled).");

        var refusal = Refusal(s);
        if (refusal != null) return new SubscriptionOutcome(false, refusal);
        var format = ReportDownloads.Find(s.Format)!;

        var raw = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in s.Parameters)
            raw[name] = value.Contains(ValueList.Separator) ? ValueList.Split(value).ToArray() : new[] { value };

        var view = await _reports.RunAsync(s.ReportId, raw, ct).ConfigureAwait(false);
        if (view.Status != RunStatus.Ok || view.Result == null) return new SubscriptionOutcome(false, view.Message);

        var file = _reports.Render(format, view.Result, s.ReportId);
        if (file.Bytes == null) return new SubscriptionOutcome(false, file.Message);
        if (file.Bytes.Length > (long)Math.Max(1, o.MaxAttachmentMb) * 1024 * 1024)
            return new SubscriptionOutcome(false, $"The {format.Label} is bigger than the {Math.Max(1, o.MaxAttachmentMb)} MB this server mails.");

        var values = Values(s, view.Definition);
        var mail = new MailRequest(
            o.Smtp.From,
            s.Recipients.ToArray(),
            SubscriptionText.Render(s.Subject, values, Subscription.MaxSubjectChars, singleLine: true),
            SubscriptionText.Render(s.Body, values, Subscription.MaxBodyChars, singleLine: false),
            s.ReportId + "." + format.Extension,
            format.ContentType.Split(';')[0],
            file.Bytes);

        try
        {
            await _mail.SendAsync(mail, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.LogError(ex, "Subscription {Id} ({Name}): the mail could not be sent", s.Id, s.Name);
            return new SubscriptionOutcome(false, "The mail could not be sent: " + ex.GetType().Name);
        }

        _log.LogInformation("Subscription {Id} ({Name}): {Report} sent to {Count} recipient(s)", s.Id, s.Name, s.ReportId, s.Recipients.Count);
        return new SubscriptionOutcome(true, "Sent.");
    }

    private Dictionary<string, string> Values(Subscription s, ReportDefinition? def)
    {
        var zone = Schedule.ResolveZone(s.Schedule.TimeZone) ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Report"] = string.IsNullOrWhiteSpace(def?.Title) ? s.ReportId : def!.Title,
            ["Name"] = s.Name,
            ["Date"] = local.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
            ["Time"] = local.ToString("HH:mm", CultureInfo.InvariantCulture)
        };
        foreach (var (name, value) in s.Parameters) values["Param." + name] = value;
        return values;
    }
}
