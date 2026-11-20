namespace ReportStudio.Core.Server;

/// <summary>
/// One standing order to mail a report (requirement 50): which report, with which parameter values, in which format, to whom, and when.
/// <see cref="Problems"/> checks the shape of it; whether the report exists and whether the server may send to those addresses is decided by
/// the program that runs it, which knows its own folder and settings.
/// </summary>
public sealed class Subscription
{
    public const int MaxNameChars = 100;
    public const int MaxRecipients = 50;
    public const int MaxParameters = 50;
    public const int MaxParameterNameChars = 100;
    public const int MaxParameterValueChars = 1000;
    public const int MaxSubjectChars = 200;
    public const int MaxBodyChars = 5000;
    public const int MaxFormatChars = 20;

    /// <summary>Assigned when the subscription is added.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The report's name in the reports folder (its file name without ".rsr").</summary>
    public string ReportId { get; set; } = "";

    /// <summary>Parameter values by parameter name.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The attachment format, such as xlsx or csv (the program that sends it says which are possible).</summary>
    public string Format { get; set; } = "xlsx";

    public List<string> Recipients { get; set; } = new();

    /// <summary>The subject, with {Report}, {Name}, {Date}, {Time} and {Param.Name} filled in.</summary>
    public string Subject { get; set; } = "{Report} - {Date}";

    /// <summary>The plain-text message, with the same fill-ins.</summary>
    public string Body { get; set; } = "The report {Report} for {Date} is attached.";

    public Schedule Schedule { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? NextRun { get; set; }
    public DateTimeOffset? LastRun { get; set; }

    /// <summary>"ok" or "failed" after a run; null before the first.</summary>
    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    /// <summary>Makes a subscription read from a file usable: no null lists, trimmed text, parameter names that ignore case.</summary>
    public void Normalize()
    {
        Id = (Id ?? "").Trim();
        Name = (Name ?? "").Trim();
        ReportId = (ReportId ?? "").Trim();
        Format = (Format ?? "").Trim().ToLowerInvariant();
        Subject = Subject ?? "";
        Body = Body ?? "";
        Schedule ??= new Schedule();
        Schedule.Days ??= new List<DayOfWeek>();
        Schedule.Time = (Schedule.Time ?? "").Trim();
        Schedule.TimeZone = (Schedule.TimeZone ?? "").Trim();
        Recipients = (Recipients ?? new List<string>()).Select(r => (r ?? "").Trim()).ToList();
        Parameters = new Dictionary<string, string>(Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What is wrong with this subscription, one sentence each; empty when it can be stored.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var copy = Clone();   // checked on a tidied copy, so null lists in a hand-edited file give sentences, not crashes
        copy.Normalize();

        if (copy.Name.Length == 0 || copy.Name.Length > MaxNameChars)
            problems.Add($"The name is needed and can have at most {MaxNameChars} characters.");
        if (copy.ReportId.Length == 0 || copy.ReportId.Length > 100)
            problems.Add("The report is needed.");
        if (copy.Format.Length == 0 || copy.Format.Length > MaxFormatChars || !copy.Format.All(char.IsAsciiLetterOrDigit))
            problems.Add("The format must be letters and digits only, like xlsx.");

        if (copy.Recipients.Count == 0) problems.Add("At least one recipient is needed.");
        if (copy.Recipients.Count > MaxRecipients) problems.Add($"There can be at most {MaxRecipients} recipients.");
        foreach (var r in copy.Recipients.Where(r => !EmailAddress.IsValid(r)))
            problems.Add($"'{Shorten(r)}' is not a valid e-mail address.");
        foreach (var dup in copy.Recipients.GroupBy(r => r, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"'{Shorten(dup.Key)}' is listed twice.");

        if (copy.Parameters.Count > MaxParameters) problems.Add($"There can be at most {MaxParameters} parameter values.");
        foreach (var (name, value) in copy.Parameters)
        {
            if (name.Length == 0 || name.Length > MaxParameterNameChars) problems.Add("A parameter name is empty or too long.");
            if ((value ?? "").Length > MaxParameterValueChars) problems.Add($"The value of parameter '{Shorten(name)}' is too long.");
        }

        if (string.IsNullOrWhiteSpace(copy.Subject) || copy.Subject.Length > MaxSubjectChars)
            problems.Add($"The subject is needed and can have at most {MaxSubjectChars} characters.");
        if (copy.Body.Length > MaxBodyChars) problems.Add($"The message can have at most {MaxBodyChars} characters.");

        problems.AddRange(copy.Schedule.Problems());
        return problems;
    }

    /// <summary>A copy that shares nothing with this one.</summary>
    public Subscription Clone() => new()
    {
        Id = Id,
        Name = Name,
        ReportId = ReportId,
        Parameters = new Dictionary<string, string>(Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        Format = Format,
        Recipients = new List<string>(Recipients ?? new List<string>()),
        Subject = Subject,
        Body = Body,
        Schedule = new Schedule
        {
            Kind = Schedule?.Kind ?? ScheduleKind.Daily,
            Time = Schedule?.Time ?? "",
            Days = new List<DayOfWeek>(Schedule?.Days ?? new List<DayOfWeek>()),
            DayOfMonth = Schedule?.DayOfMonth ?? 1,
            IntervalMinutes = Schedule?.IntervalMinutes ?? 60,
            TimeZone = Schedule?.TimeZone ?? ""
        },
        Enabled = Enabled,
        NextRun = NextRun,
        LastRun = LastRun,
        LastStatus = LastStatus,
        LastError = LastError
    };

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "...";
}
