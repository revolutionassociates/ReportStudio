using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>When a subscription runs (requirement 50). Time zones are built here, so the tests do not depend on the computer's zone data.</summary>
public class ScheduleTests
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.CreateCustomTimeZone("TestIST", TimeSpan.FromHours(5.5), "IST", "IST");

    // A zone like US Eastern: UTC-5, clocks forward on the second Sunday of March (2026-03-08) and back on the first Sunday of November (2026-11-01), both at 02:00.
    private static readonly TimeZoneInfo Dst = TimeZoneInfo.CreateCustomTimeZone(
        "TestDST", TimeSpan.FromHours(-5), "DST", "ST", "DT",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))
        });

    private static DateTimeOffset Ist_(int y, int m, int d, int h, int min, int s = 0) =>
        new(y, m, d, h, min, s, TimeSpan.FromHours(5.5));

    [Fact]
    public void Daily_RunsToday_WhenTheTimeIsStillAhead()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Time = "08:00" };
        Assert.Equal(Ist_(2026, 11, 19, 8, 0), s.NextRun(Ist_(2026, 11, 19, 6, 0), Ist));
    }

    [Fact]
    public void Daily_RunsTomorrow_WhenTheTimeHasPassed_AndIsStrictlyAfter()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Time = "08:00" };
        Assert.Equal(Ist_(2026, 11, 20, 8, 0), s.NextRun(Ist_(2026, 11, 19, 8, 0), Ist));
        Assert.Equal(Ist_(2026, 11, 20, 8, 0), s.NextRun(Ist_(2026, 11, 19, 9, 30), Ist));
    }

    [Fact]
    public void Weekly_PicksTheNextListedWeekday()
    {
        var s = new Schedule { Kind = ScheduleKind.Weekly, Time = "08:00", Days = { DayOfWeek.Monday, DayOfWeek.Friday } };
        // 2026-11-18 is a Wednesday
        Assert.Equal(Ist_(2026, 11, 20, 8, 0), s.NextRun(Ist_(2026, 11, 18, 10, 0), Ist));
        Assert.Equal(Ist_(2026, 11, 23, 8, 0), s.NextRun(Ist_(2026, 11, 20, 9, 0), Ist));
    }

    [Fact]
    public void Monthly_UsesTheLastDayOfAShortMonth()
    {
        var s = new Schedule { Kind = ScheduleKind.Monthly, Time = "06:00", DayOfMonth = 31 };
        Assert.Equal(Ist_(2026, 2, 28, 6, 0), s.NextRun(Ist_(2026, 2, 1, 0, 0), Ist));
        Assert.Equal(Ist_(2026, 3, 31, 6, 0), s.NextRun(Ist_(2026, 2, 28, 7, 0), Ist));
    }

    [Fact]
    public void Monthly_FirstDay_GoesToNextMonthWhenPassed()
    {
        var s = new Schedule { Kind = ScheduleKind.Monthly, Time = "09:00", DayOfMonth = 1 };
        Assert.Equal(Ist_(2026, 12, 1, 9, 0), s.NextRun(Ist_(2026, 11, 1, 9, 0), Ist));
    }

    [Fact]
    public void Interval_AddsMinutesToTheWholeSecond()
    {
        var s = new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = 30 };
        var after = Ist_(2026, 11, 19, 10, 0, 30).AddTicks(7_000_000);   // 10:00:30.7
        Assert.Equal(Ist_(2026, 11, 19, 10, 30, 30), s.NextRun(after, Ist));
    }

    [Fact]
    public void Dst_KeepsTheWallClockTime()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Time = "08:00" };
        var before = s.NextRun(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), Dst)!.Value;   // 09:00 local, UTC-5 -> next 08:00 on Mar 8 is -4
        Assert.Equal(new DateTime(2026, 3, 8, 12, 0, 0), before.UtcDateTime);                          // 08:00 at UTC-4
        var winter = s.NextRun(new DateTimeOffset(2026, 3, 6, 14, 0, 0, TimeSpan.Zero), Dst)!.Value;
        Assert.Equal(new DateTime(2026, 3, 7, 13, 0, 0), winter.UtcDateTime);                          // 08:00 at UTC-5
    }

    [Fact]
    public void Dst_TimeThatDoesNotExist_RunsWhenTheClockReachesTheNextHour()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Time = "02:30" };   // 02:30 does not exist on 2026-03-08
        var next = s.NextRun(new DateTimeOffset(2026, 3, 7, 18, 0, 0, TimeSpan.Zero), Dst)!.Value;
        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0), next.UtcDateTime);   // 03:30 at UTC-4
    }

    [Fact]
    public void Dst_TimeThatHappensTwice_RunsOnlyTheFirstTime()
    {
        var s = new Schedule { Kind = ScheduleKind.Daily, Time = "01:30" };   // 01:30 happens twice on 2026-11-01
        var first = s.NextRun(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero), Dst)!.Value;
        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0), first.UtcDateTime);   // 01:30 at UTC-4
        var next = s.NextRun(first, Dst)!.Value;
        Assert.Equal(new DateTime(2026, 11, 2, 6, 30, 0), next.UtcDateTime);    // the next day, not 01:30 at UTC-5 the same night
    }

    [Theory]
    [InlineData("08:00", true)]
    [InlineData("8:05", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("08:60", false)]
    [InlineData("8", false)]
    [InlineData("", false)]
    [InlineData("08:00:00", false)]
    public void TryParseTime_AcceptsOnlyClockTimes(string text, bool ok) =>
        Assert.Equal(ok, Schedule.TryParseTime(text, out _));

    [Fact]
    public void Problems_ReportEachMistake()
    {
        Assert.Empty(new Schedule { Kind = ScheduleKind.Daily, Time = "08:00", TimeZone = "UTC" }.Problems());
        Assert.Single(new Schedule { Kind = ScheduleKind.Daily, Time = "25:00", TimeZone = "UTC" }.Problems());
        Assert.Single(new Schedule { Kind = ScheduleKind.Weekly, Time = "08:00", TimeZone = "UTC" }.Problems());
        Assert.Single(new Schedule { Kind = ScheduleKind.Monthly, Time = "08:00", DayOfMonth = 32, TimeZone = "UTC" }.Problems());
        Assert.Single(new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = 5, TimeZone = "UTC" }.Problems());
        Assert.Single(new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = Schedule.MaxIntervalMinutes + 1, TimeZone = "UTC" }.Problems());
        Assert.Single(new Schedule { Kind = ScheduleKind.Daily, Time = "08:00", TimeZone = "Nowhere/Land" }.Problems());
        Assert.Single(new Schedule { Kind = (ScheduleKind)99, TimeZone = "UTC" }.Problems());
    }

    [Fact]
    public void NextRun_IsNull_ForAnUnusableSchedule()
    {
        var after = Ist_(2026, 11, 19, 10, 0);
        Assert.Null(new Schedule { Kind = ScheduleKind.Daily, Time = "nope" }.NextRun(after, Ist));
        Assert.Null(new Schedule { Kind = ScheduleKind.Weekly, Time = "08:00" }.NextRun(after, Ist));
        Assert.Null(new Schedule { Kind = ScheduleKind.Daily, Time = "08:00" }.NextRun(after, null));
    }

    [Fact]
    public void Describe_SaysWhatRuns()
    {
        Assert.Equal("Every day at 08:00 (UTC)", new Schedule { Time = "08:00", TimeZone = "UTC" }.Describe());
        Assert.Equal("Every 30 minutes", new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = 30 }.Describe());
        Assert.Contains("Friday", new Schedule { Kind = ScheduleKind.Weekly, Days = { DayOfWeek.Friday } }.Describe());
    }

    [Fact]
    public void ResolveZone_FindsUtc_AndRefusesNonsense()
    {
        Assert.NotNull(Schedule.ResolveZone("UTC"));
        Assert.Null(Schedule.ResolveZone("Nowhere/Land"));
    }
}
