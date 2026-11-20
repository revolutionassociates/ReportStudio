using System.Text.Json;
using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Subscription text, shape checks and the subscription file (requirement 50).</summary>
public sealed class SubscriptionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rs-sub-" + Guid.NewGuid().ToString("N"));

    public SubscriptionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private sealed class ClockForTests : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private string FilePath => Path.Combine(_folder, "subs.json");

    private static Subscription Valid() => new()
    {
        Name = "Daily sales",
        ReportId = "sales",
        Format = "xlsx",
        Recipients = { "a@example.com" },
        Schedule = new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = 60, TimeZone = "UTC" }
    };

    // ---- text ----

    [Fact]
    public void Text_FillsKnownTokens_AndKeepsUnknownOnes()
    {
        var values = new Dictionary<string, string> { ["Report"] = "Sales", ["Date"] = "19-11-2026", ["Param.Company"] = "Acme" };
        var text = SubscriptionText.Render("{Report} {report} {Param.Company} {Nope} {Date}", values, 200, singleLine: true);
        Assert.Equal("Sales Sales Acme {Nope} 19-11-2026", text);
    }

    [Fact]
    public void Text_SingleLine_TurnsLineBreaksIntoSpaces()
    {
        var values = new Dictionary<string, string> { ["Param.X"] = "a\r\nBcc: evil@example.com\tb" };
        var text = SubscriptionText.Render("Hi {Param.X}\nthere", values, 200, singleLine: true);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\n', text);
        Assert.Equal("Hi a Bcc: evil@example.com b there", text);
    }

    [Fact]
    public void Text_Body_KeepsLineBreaks_AndIsCut()
    {
        var values = new Dictionary<string, string>();
        Assert.Equal("one\ntwo", SubscriptionText.Render("one\ntwo", values, 200, singleLine: false));
        Assert.Equal("abc", SubscriptionText.Render("abcdef", values, 3, singleLine: false));
        Assert.Equal("", SubscriptionText.Render(null, values, 10, singleLine: true));
    }

    // ---- shape ----

    [Fact]
    public void Problems_ValidSubscription_HasNone() => Assert.Empty(Valid().Problems());

    [Fact]
    public void Problems_ListEachMistake()
    {
        var s = Valid();
        s.Name = "";
        s.ReportId = "";
        s.Format = "x/ls";
        s.Recipients = new List<string> { "bad", "a@example.com", "A@example.com" };
        s.Subject = " ";
        var problems = s.Problems();
        Assert.Equal(6, problems.Count);
    }

    [Fact]
    public void Problems_NoRecipients_AndTooManyParameters()
    {
        var s = Valid();
        s.Recipients.Clear();
        Assert.Single(s.Problems());

        s = Valid();
        for (int i = 0; i < Subscription.MaxParameters + 1; i++) s.Parameters["p" + i] = "v";
        Assert.Single(s.Problems());
    }

    [Fact]
    public void Problems_SurviveNullsFromAHandEditedFile()
    {
        var s = Valid();
        s.Recipients = null!;
        s.Parameters = null!;
        s.Schedule = null!;
        s.Subject = null!;
        var problems = s.Problems();   // must not throw
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void Clone_SharesNothing()
    {
        var a = Valid();
        a.Parameters["Company"] = "Acme";
        var b = a.Clone();
        b.Recipients.Add("b@example.com");
        b.Parameters["Company"] = "Other";
        b.Schedule.Days.Add(DayOfWeek.Monday);
        Assert.Single(a.Recipients);
        Assert.Equal("Acme", a.Parameters["Company"]);
        Assert.Empty(a.Schedule.Days);
    }

    // ---- store ----

    [Fact]
    public void Store_MissingFile_IsEmpty() => Assert.Empty(new SubscriptionStore(FilePath).All());

    [Fact]
    public void Store_Add_GivesIdAndFirstRun_AndKeepsThemInTheFile()
    {
        var clock = new ClockForTests();
        var store = new SubscriptionStore(FilePath, clock);
        var added = store.Add(Valid());

        Assert.Equal(32, added.Id.Length);
        Assert.Equal(clock.Now.AddMinutes(60), added.NextRun);
        Assert.Null(added.LastRun);

        var again = new SubscriptionStore(FilePath, clock).Get(added.Id);   // a different program instance reads the same file
        Assert.NotNull(again);
        Assert.Equal("Daily sales", again.Name);
        Assert.Equal(ScheduleKind.Interval, again.Schedule.Kind);
        Assert.Equal(["a@example.com"], again.Recipients);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Store_Add_RefusesAnInvalidSubscription_AndWritesNothing()
    {
        var store = new SubscriptionStore(FilePath);
        var bad = Valid();
        bad.Recipients = new List<string> { "nope" };
        var ex = Assert.Throws<ArgumentException>(() => store.Add(bad));
        Assert.Contains("not a valid e-mail address", ex.Message);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Store_Add_IgnoresAnIdAndHistorySuppliedByTheCaller()
    {
        var store = new SubscriptionStore(FilePath);
        var s = Valid();
        s.Id = "chosen";
        s.LastStatus = "ok";
        var added = store.Add(s);
        Assert.NotEqual("chosen", added.Id);
        Assert.Null(added.LastStatus);
    }

    [Fact]
    public void Store_Due_ListsOnlyEnabledOnesThatAreDue_OldestFirst()
    {
        var clock = new ClockForTests();
        var store = new SubscriptionStore(FilePath, clock);
        var early = store.Add(Valid());
        clock.Now = clock.Now.AddMinutes(10);
        var late = store.Add(Valid());
        var off = Valid();
        off.Enabled = false;
        store.Add(off);

        Assert.Empty(store.Due(clock.Now));
        var due = store.Due(clock.Now.AddMinutes(120));
        Assert.Equal([early.Id, late.Id], due.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Store_RecordRun_SetsHistory_AndNextRunComesFromNow()
    {
        var clock = new ClockForTests();
        var store = new SubscriptionStore(FilePath, clock);
        var added = store.Add(Valid());

        clock.Now = clock.Now.AddDays(7);   // the server was off for a week: one run, not 168
        Assert.True(store.RecordRun(added.Id, ok: true, error: null));
        var after = store.Get(added.Id)!;
        Assert.Equal("ok", after.LastStatus);
        Assert.Equal(clock.Now, after.LastRun);
        Assert.Equal(clock.Now.AddMinutes(60), after.NextRun);
        Assert.Empty(store.Due(clock.Now));
    }

    [Fact]
    public void Store_RecordRun_Failure_KeepsAShortError_AndUnknownIdIsFalse()
    {
        var store = new SubscriptionStore(FilePath);
        var added = store.Add(Valid());
        Assert.True(store.RecordRun(added.Id, ok: false, error: new string('x', 1000)));
        var after = store.Get(added.Id)!;
        Assert.Equal("failed", after.LastStatus);
        Assert.Equal(300, after.LastError!.Length);
        Assert.False(store.RecordRun("missing", true, null));
    }

    [Fact]
    public void Store_Update_KeepsHistory_ChangesTheRest_AndRecomputesNextRun()
    {
        var clock = new ClockForTests();
        var store = new SubscriptionStore(FilePath, clock);
        var added = store.Add(Valid());
        store.RecordRun(added.Id, ok: true, error: null);

        var edit = store.Get(added.Id)!;
        edit.Name = "Renamed";
        edit.Schedule.IntervalMinutes = 30;
        edit.LastStatus = "forged";   // history is the server's, not the caller's
        Assert.True(store.Update(edit));

        var after = store.Get(added.Id)!;
        Assert.Equal("Renamed", after.Name);
        Assert.Equal("ok", after.LastStatus);
        Assert.Equal(clock.Now.AddMinutes(30), after.NextRun);

        var gone = Valid();
        gone.Id = "missing";
        Assert.False(store.Update(gone));
    }

    [Fact]
    public void Store_Disabling_ClearsNextRun()
    {
        var store = new SubscriptionStore(FilePath);
        var added = store.Add(Valid());
        added.Enabled = false;
        store.Update(added);
        Assert.Null(store.Get(added.Id)!.NextRun);
    }

    [Fact]
    public void Store_Remove()
    {
        var store = new SubscriptionStore(FilePath);
        var added = store.Add(Valid());
        Assert.True(store.Remove(added.Id));
        Assert.False(store.Remove(added.Id));
        Assert.Empty(store.All());
    }

    [Fact]
    public void Store_ReturnedCopies_DoNotChangeTheFile()
    {
        var store = new SubscriptionStore(FilePath);
        var added = store.Add(Valid());
        added.Name = "changed in memory";
        added.Recipients.Add("x@example.com");
        Assert.Equal("Daily sales", store.Get(added.Id)!.Name);
        Assert.Single(store.Get(added.Id)!.Recipients);
    }

    [Fact]
    public void Store_DamagedFile_IsAnError_NotAnEmptyList()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        var store = new SubscriptionStore(FilePath);
        Assert.Throws<InvalidDataException>(() => store.All());
        Assert.Throws<InvalidDataException>(() => store.Add(Valid()));
        Assert.Equal("{ this is not json", File.ReadAllText(FilePath));   // not overwritten
    }

    [Fact]
    public void Store_TwoEntriesWithTheSameId_IsAnError()
    {
        File.WriteAllText(FilePath, """
            { "subscriptions": [
              { "id": "one", "name": "A", "reportId": "r", "format": "csv", "recipients": ["a@example.com"], "schedule": { "kind": "Interval", "intervalMinutes": 60, "timeZone": "UTC" } },
              { "id": "one", "name": "B", "reportId": "r", "format": "csv", "recipients": ["a@example.com"], "schedule": { "kind": "Interval", "intervalMinutes": 60, "timeZone": "UTC" } }
            ] }
            """);
        Assert.Throws<InvalidDataException>(() => new SubscriptionStore(FilePath).All());
    }

    [Fact]
    public void Store_HandWrittenEntry_GetsAnIdAndFirstRun_ThatStayTheSame()
    {
        File.WriteAllText(FilePath, """
            { "subscriptions": [
              { "name": "By hand", "reportId": "r", "format": "csv", "recipients": ["a@example.com"], "schedule": { "kind": "Interval", "intervalMinutes": 30, "timeZone": "UTC" } }
            ] }
            """);
        var clock = new ClockForTests();
        var store = new SubscriptionStore(FilePath, clock);

        var first = store.All().Single();
        Assert.Equal(32, first.Id.Length);
        Assert.Equal(clock.Now.AddMinutes(30), first.NextRun);

        clock.Now = clock.Now.AddMinutes(45);   // later reads must not push the first run away again
        var second = store.All().Single();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.NextRun, second.NextRun);
        Assert.Single(store.Due(clock.Now));
    }

    [Fact]
    public void Store_FileIsPlainJson_WithEnumNames()
    {
        var store = new SubscriptionStore(FilePath);
        store.Add(Valid());
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        var schedule = doc.RootElement.GetProperty("subscriptions")[0].GetProperty("schedule");
        Assert.Equal("Interval", schedule.GetProperty("kind").GetString());
    }
}
