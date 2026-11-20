using System.Globalization;

namespace ReportStudio.Core.Server;

public enum ScheduleKind { Daily, Weekly, Monthly, Interval }

/// <summary>
/// When a subscription runs (requirement 50). Daily, weekly and monthly runs happen at a clock time in a named time zone, so "08:00" stays
/// 08:00 on the wall clock when the clocks change; an interval run repeats a fixed number of minutes after the last one.
/// A time that does not exist on a day the clocks jump forward runs when the clock first reaches an hour that does, and a time that happens
/// twice runs only the first time. Nothing here reads the clock: the caller says what "after" means, which keeps it testable.
/// </summary>
public sealed class Schedule
{
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 7 * 24 * 60;
    public const string DefaultTimeZone = "Asia/Kolkata";

    private const int SearchDays = 800;   // far enough to find the next 29 February or 31st

    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;

    /// <summary>The clock time as HH:mm (24 hours) for daily, weekly and monthly runs.</summary>
    public string Time { get; set; } = "08:00";

    /// <summary>The weekdays of a weekly run.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>The day of a monthly run, 1 to 31. A month without that day uses its last day.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>The minutes between runs of an interval schedule.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>A time-zone id, either IANA (Asia/Kolkata) or Windows (India Standard Time).</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The time zone with this id (IANA or Windows spelling), or null when the system does not know it.</summary>
    public static TimeZoneInfo? ResolveZone(string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? DefaultTimeZone : id.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) && windows != null
            && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out zone)) return zone;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && iana != null
            && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out zone)) return zone;
        return null;
    }

    /// <summary>What is wrong with this schedule, one sentence each; empty when it can be used.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>(ShapeProblems());
        if (ResolveZone(TimeZone) == null) problems.Add($"The time zone '{TimeZone}' is not known on this computer.");
        return problems;
    }

    private List<string> ShapeProblems()
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(Kind))
        {
            problems.Add("The schedule kind is not one of Daily, Weekly, Monthly or Interval.");
            return problems;
        }

        if (Kind != ScheduleKind.Interval && !TryParseTime(Time, out _))
            problems.Add($"The time '{Time}' is not a 24-hour time like 08:30.");

        if (Kind == ScheduleKind.Weekly)
        {
            if (Days == null || Days.Count == 0) problems.Add("A weekly schedule needs at least one weekday.");
            else if (Days.Any(d => !Enum.IsDefined(d))) problems.Add("A weekday is not valid.");
        }

        if (Kind == ScheduleKind.Monthly && (DayOfMonth < 1 || DayOfMonth > 31))
            problems.Add("The day of the month must be from 1 to 31.");

        if (Kind == ScheduleKind.Interval && (IntervalMinutes < MinIntervalMinutes || IntervalMinutes > MaxIntervalMinutes))
            problems.Add($"The interval must be from {MinIntervalMinutes} to {MaxIntervalMinutes} minutes.");

        return problems;
    }

    /// <summary>A plain-words description for lists and logs.</summary>
    public string Describe() => Kind switch
    {
        ScheduleKind.Daily => $"Every day at {Time} ({TimeZone})",
        ScheduleKind.Weekly => $"Every {string.Join(", ", (Days ?? new List<DayOfWeek>()).Distinct().Order())} at {Time} ({TimeZone})",
        ScheduleKind.Monthly => $"On day {DayOfMonth} of every month at {Time} ({TimeZone})",
        ScheduleKind.Interval => $"Every {IntervalMinutes} minutes",
        _ => "Invalid schedule"
    };

    /// <summary>The first run strictly after <paramref name="after"/>, or null when the schedule cannot be used.</summary>
    public DateTimeOffset? NextRun(DateTimeOffset after) => NextRun(after, ResolveZone(TimeZone));

    /// <summary>The same, in a time zone the caller supplies.</summary>
    public DateTimeOffset? NextRun(DateTimeOffset after, TimeZoneInfo? zone)
    {
        if (ShapeProblems().Count > 0) return null;

        if (Kind == ScheduleKind.Interval)
        {
            var whole = new DateTimeOffset(after.Ticks - after.Ticks % TimeSpan.TicksPerSecond, after.Offset);
            return whole.AddMinutes(IntervalMinutes);
        }

        if (zone == null || !TryParseTime(Time, out var time)) return null;

        var local = TimeZoneInfo.ConvertTime(after, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        for (int i = 0; i < SearchDays; i++, date = date.AddDays(1))
        {
            if (!RunsOn(date)) continue;
            var candidate = At(date, time, zone);
            if (candidate > after) return candidate;
        }
        return null;
    }

    private bool RunsOn(DateOnly date) => Kind switch
    {
        ScheduleKind.Daily => true,
        ScheduleKind.Weekly => Days.Contains(date.DayOfWeek),
        ScheduleKind.Monthly => date.Day == Math.Min(DayOfMonth, DateTime.DaysInMonth(date.Year, date.Month)),
        _ => false
    };

    private static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var wall = date.ToDateTime(time);   // a clock time with no zone
        for (int hour = 0; hour < 3 && zone.IsInvalidTime(wall); hour++) wall = wall.AddHours(1);   // the clocks jump over it

        // a time that happens twice: take the first, which has the larger offset
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }
}
