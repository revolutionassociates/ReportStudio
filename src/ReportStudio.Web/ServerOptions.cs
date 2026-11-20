namespace ReportStudio.Web;

/// <summary>The mail server a report is sent through.</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>The sender address of every report mail.</summary>
    public string From { get; set; } = "";
}

/// <summary>
/// The settings of the scheduled-report server (requirement 50), read from the "ReportStudio:Server" section. It is off until the owner turns it
/// on, and it sends nothing anywhere the owner has not named: <see cref="AllowedRecipientDomains"/> is required, and an empty list allows no mail at all.
/// Keep the SMTP password and the admin key out of appsettings.json in a shared folder; use environment variables
/// (<c>ReportStudio__Server__Smtp__Password</c>, <c>ReportStudio__Server__AdminKey</c>) or a secret store.
/// </summary>
public sealed class ServerOptions
{
    public const int MinAdminKeyChars = 16;

    public bool Enabled { get; set; }

    /// <summary>The JSON file that holds the subscriptions. A relative path is taken from the program's working folder.</summary>
    public string SubscriptionsFile { get; set; } = "subscriptions.json";

    /// <summary>How often the server looks for subscriptions that are due (seconds, at least 5).</summary>
    public int PollSeconds { get; set; } = 30;

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Reports may only be mailed to these domains (and their sub-domains), such as "example.com".</summary>
    public List<string> AllowedRecipientDomains { get; set; } = new();

    /// <summary>The key the admin API asks for in the X-Admin-Key header. Shorter than <see cref="MinAdminKeyChars"/> characters, or empty: there is no admin API.</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>The biggest attachment that is mailed (megabytes).</summary>
    public int MaxAttachmentMb { get; set; } = 20;

    public bool AdminEnabled => Enabled && (AdminKey ?? "").Length >= MinAdminKeyChars;
}
