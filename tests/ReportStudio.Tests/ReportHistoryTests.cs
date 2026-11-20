using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The saved versions of a report (requirement 51).</summary>
public sealed class ReportHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rs-hist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private sealed class ClockForTests : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("sales", true)]
    [InlineData("Sales Report (2026)_v1.final", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData(".hidden", false)]
    [InlineData(" lead", false)]
    [InlineData("trail ", false)]
    [InlineData("trail.", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    [InlineData("é", false)]
    public void IsValidId_FollowsTheWebViewersRule(string? id, bool ok) => Assert.Equal(ok, ReportHistory.IsValidId(id));

    [Fact]
    public void IsValidId_RefusesTooLong() => Assert.False(ReportHistory.IsValidId(new string('a', 101)));

    [Fact]
    public void Record_NumbersVersionsFromOne_AndListsNewestFirst()
    {
        var clock = new ClockForTests();
        var history = new ReportHistory(_folder, clock);

        var one = history.Record("sales", "{\"a\":1}", "Asha", "first");
        clock.Now = clock.Now.AddHours(1);
        var two = history.Record("sales", "{\"a\":2}", "Ravi", "second");

        Assert.Equal(1, one.Number);
        Assert.Equal(2, two.Number);
        var list = history.List("sales");
        Assert.Equal([2, 1], list.Select(v => v.Number).ToArray());
        Assert.Equal("Ravi", list[0].Author);
        Assert.Equal("first", list[1].Note);
        Assert.Equal(clock.Now, list[0].SavedAt);
    }

    [Fact]
    public void Record_TheSameTextAgain_MakesNoNewVersion()
    {
        var history = new ReportHistory(_folder);
        var first = history.Record("sales", "{\"a\":1}");
        var again = history.Record("sales", "{\"a\":1}", "Someone else", "ignored");
        Assert.Equal(first, again);
        Assert.Single(history.List("sales"));

        history.Record("sales", "{\"a\":2}");
        var back = history.Record("sales", "{\"a\":1}");   // going back to older text is a new version
        Assert.Equal(3, back.Number);
    }

    [Fact]
    public void Read_ReturnsTheExactText_AndNullForUnknown()
    {
        var history = new ReportHistory(_folder);
        const string text = "{\n  \"name\": \"Sales — रिपोर्ट\"\n}";
        history.Record("sales", text);
        Assert.Equal(text, history.Read("sales", 1));
        Assert.Null(history.Read("sales", 2));
        Assert.Null(history.Read("other", 1));
    }

    [Fact]
    public void Read_ADamagedVersion_IsAnError()
    {
        var history = new ReportHistory(_folder);
        history.Record("sales", "{\"a\":1}");
        File.WriteAllText(Path.Combine(_folder, "sales", "v0001.rsr"), "{\"a\":999}");
        Assert.Throws<InvalidDataException>(() => history.Read("sales", 1));
    }

    [Fact]
    public void Read_AMissingFile_IsAnError()
    {
        var history = new ReportHistory(_folder);
        history.Record("sales", "{\"a\":1}");
        File.Delete(Path.Combine(_folder, "sales", "v0001.rsr"));
        Assert.Throws<InvalidDataException>(() => history.Read("sales", 1));
    }

    [Fact]
    public void List_SkipsADamagedDescription_AndIgnoresStrangeFiles()
    {
        var history = new ReportHistory(_folder);
        history.Record("sales", "{\"a\":1}");
        history.Record("sales", "{\"a\":2}");
        File.WriteAllText(Path.Combine(_folder, "sales", "v0001.json"), "{ not json");
        File.WriteAllText(Path.Combine(_folder, "sales", "vabcd.json"), "{}");
        File.WriteAllText(Path.Combine(_folder, "sales", "notes.txt"), "x");
        Assert.Equal([2], history.List("sales").Select(v => v.Number).ToArray());
    }

    [Fact]
    public void Record_NumbersContinueAfterPruning()
    {
        var history = new ReportHistory(_folder);
        for (int i = 1; i <= 4; i++) history.Record("sales", "{\"a\":" + i + "}");
        Assert.Equal(3, history.Prune("sales", 1));
        Assert.Equal([4], history.List("sales").Select(v => v.Number).ToArray());
        Assert.Equal(5, history.Record("sales", "{\"a\":5}").Number);
        foreach (var n in new[] { "v0001", "v0002", "v0003" })
        {
            Assert.False(File.Exists(Path.Combine(_folder, "sales", n + ".rsr")));
            Assert.False(File.Exists(Path.Combine(_folder, "sales", n + ".json")));
        }
    }

    [Fact]
    public void Prune_RefusesToKeepNothing_AndKeepsWhatIsAsked()
    {
        var history = new ReportHistory(_folder);
        history.Record("sales", "{\"a\":1}");
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Prune("sales", 0));
        Assert.Equal(0, history.Prune("sales", 5));
        Assert.Equal(0, history.Prune("nothing-here", 5));
    }

    [Fact]
    public void Reports_KeepSeparateHistories()
    {
        var history = new ReportHistory(_folder);
        history.Record("a", "{\"x\":1}");
        history.Record("b", "{\"x\":1}");
        history.Record("b", "{\"x\":2}");
        Assert.Single(history.List("a"));
        Assert.Equal(2, history.List("b").Count);
    }

    [Fact]
    public void Record_CleansTheAuthorAndNote_AndCutsThem()
    {
        var history = new ReportHistory(_folder);
        var v = history.Record("sales", "{\"a\":1}", "Asha\r\nBcc: x", new string('n', 900));
        Assert.Equal("Asha Bcc: x", v.Author);
        Assert.Equal(ReportHistory.MaxNoteChars, v.Note.Length);
        Assert.Equal("", history.Record("sales", "{\"a\":2}", null, null).Author);
    }

    [Fact]
    public void Record_Refuses_BadIds_EmptyText_AndHugeText()
    {
        var history = new ReportHistory(_folder);
        Assert.Throws<ArgumentException>(() => history.Record("../x", "{}"));
        Assert.Throws<ArgumentException>(() => history.Record("sales", "  "));
        Assert.Throws<ArgumentException>(() => history.Record("sales", new string('x', ReportHistory.MaxVersionBytes + 1)));
        Assert.Throws<ArgumentException>(() => history.List(".."));
        Assert.Throws<ArgumentException>(() => history.Read("a/b", 1));
        Assert.False(Directory.Exists(_folder));   // nothing was created by the refused calls
    }

    [Fact]
    public void Record_LeavesNoTemporaryFiles()
    {
        var history = new ReportHistory(_folder);
        history.Record("sales", "{\"a\":1}");
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "sales"), "*.tmp"));
    }
}
