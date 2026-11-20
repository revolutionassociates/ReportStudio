using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>Running due subscriptions: what is mailed, what is refused, and what is written back to the file (requirement 50).</summary>
public sealed class SchedulerTests : IDisposable
{
    private readonly ServerHarness _h;

    public SchedulerTests() => _h = new ServerHarness();
    public void Dispose() => _h.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Subscription Sub(Action<Subscription>? tweak = null)
    {
        var s = new Subscription
        {
            Name = "Daily sales",
            ReportId = "sales",
            Format = "csv",
            Recipients = { "a@example.com" },
            Schedule = new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = 60, TimeZone = "UTC" }
        };
        tweak?.Invoke(s);
        return s;
    }

    private SchedulerService Scheduler()
    {
        var sp = _h.Factory.Services;
        return new SchedulerService(sp.GetRequiredService<SubscriptionStore>(), sp.GetRequiredService<SubscriptionRunner>(),
            sp.GetRequiredService<IOptions<ServerOptions>>(), _h.Clock, NullLogger<SchedulerService>.Instance);
    }

    private SubscriptionStore Store => _h.Factory.Services.GetRequiredService<SubscriptionStore>();

    [Fact]
    public async Task A_due_subscription_is_mailed_and_its_next_run_moves_on()
    {
        var added = Store.Add(Sub());
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);

        Assert.Equal(1, await Scheduler().RunDueAsync(Ct));

        var mail = Assert.Single(_h.Mail.Sent);
        Assert.Equal(["a@example.com"], mail.Recipients);
        Assert.Equal("reports@example.com", mail.From);
        Assert.Equal("sales.csv", mail.AttachmentName);
        Assert.Equal("text/csv", mail.AttachmentType);
        Assert.NotEmpty(mail.Attachment);
        Assert.StartsWith("Sales by Country and Customer - 19-Nov-2026", mail.Subject);

        var after = Store.Get(added.Id)!;
        Assert.Equal("ok", after.LastStatus);
        Assert.Equal(_h.Clock.Now.AddMinutes(60), after.NextRun);
        Assert.Empty(Store.Due(_h.Clock.Now));
    }

    [Fact]
    public async Task Nothing_is_mailed_before_the_time()
    {
        Store.Add(Sub());
        _h.Clock.Now = _h.Clock.Now.AddMinutes(30);
        Assert.Equal(0, await Scheduler().RunDueAsync(Ct));
        Assert.Empty(_h.Mail.Sent);
    }

    [Fact]
    public async Task A_disabled_subscription_is_not_mailed()
    {
        Store.Add(Sub(s => s.Enabled = false));
        _h.Clock.Now = _h.Clock.Now.AddDays(2);
        Assert.Equal(0, await Scheduler().RunDueAsync(Ct));
        Assert.Empty(_h.Mail.Sent);
    }

    [Fact]
    public async Task Parameter_values_and_texts_are_filled_in()
    {
        Store.Add(Sub(s =>
        {
            s.Parameters["company"] = "Globex";
            s.Subject = "For {Param.Company}";
            s.Body = "Line one\nLine two for {Name}";
        }));
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);
        await Scheduler().RunDueAsync(Ct);

        var mail = Assert.Single(_h.Mail.Sent);
        Assert.Equal("For Globex", mail.Subject);
        Assert.Equal("Line one\nLine two for Daily sales", mail.Body);
    }

    [Fact]
    public async Task A_recipient_outside_the_allowed_domains_stops_the_whole_mail()
    {
        var added = Store.Add(Sub(s => s.Recipients = new List<string> { "a@example.com", "x@other.org" }));
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);
        await Scheduler().RunDueAsync(Ct);

        Assert.Empty(_h.Mail.Sent);
        var after = Store.Get(added.Id)!;
        Assert.Equal("failed", after.LastStatus);
        Assert.Contains("may not send", after.LastError);
    }

    [Fact]
    public async Task A_mail_failure_is_recorded_without_the_mail_servers_words()
    {
        _h.Mail.Fail = new IOException("secret smtp detail: password for bob rejected");
        var added = Store.Add(Sub());
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);
        await Scheduler().RunDueAsync(Ct);

        var after = Store.Get(added.Id)!;
        Assert.Equal("failed", after.LastStatus);
        Assert.Equal("The mail could not be sent: IOException", after.LastError);
        Assert.NotNull(after.NextRun);   // it tries again at the next scheduled time
    }

    [Fact]
    public async Task A_missing_report_and_a_wrong_parameter_are_recorded_as_failures()
    {
        var gone = Store.Add(Sub(s => s.ReportId = "no-such-report"));
        var wrong = Store.Add(Sub(s => s.Parameters["Nope"] = "x"));
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);
        Assert.Equal(2, await Scheduler().RunDueAsync(Ct));

        Assert.Equal("There is no such report.", Store.Get(gone.Id)!.LastError);
        Assert.Contains("no parameter named", Store.Get(wrong.Id)!.LastError);
        Assert.Empty(_h.Mail.Sent);
    }

    [Fact]
    public async Task A_format_the_server_does_not_offer_is_refused()
    {
        var added = Store.Add(Sub(s => s.Format = "pdf"));
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);
        await Scheduler().RunDueAsync(Ct);
        Assert.Contains("not offered", Store.Get(added.Id)!.LastError);
        Assert.Empty(_h.Mail.Sent);
    }

    [Fact]
    public async Task A_damaged_subscription_file_is_survived_and_left_alone()
    {
        File.WriteAllText(_h.SubscriptionsFile, "{ not json");
        Assert.Equal(0, await Scheduler().RunDueAsync(Ct));
        Assert.Equal("{ not json", File.ReadAllText(_h.SubscriptionsFile));
    }

    [Fact]
    public async Task One_failing_subscription_does_not_stop_the_next()
    {
        Store.Add(Sub(s => s.ReportId = "no-such-report"));
        Store.Add(Sub());
        _h.Clock.Now = _h.Clock.Now.AddMinutes(61);
        Assert.Equal(2, await Scheduler().RunDueAsync(Ct));
        Assert.Single(_h.Mail.Sent);
    }

    [Fact]
    public async Task When_the_server_is_switched_off_nothing_runs()
    {
        using var off = new ServerHarness(o => o.Enabled = false);
        var runner = off.Factory.Services.GetRequiredService<SubscriptionRunner>();
        var outcome = await runner.RunAsync(Sub(s => s.Id = "x"), Ct);
        Assert.False(outcome.Ok);
        Assert.Contains("switched off", outcome.Message);
        Assert.Empty(off.Mail.Sent);
    }

    [Fact]
    public void An_empty_allowed_domain_list_sends_nothing()
    {
        using var none = new ServerHarness(o => o.AllowedRecipientDomains.Clear());
        var runner = none.Factory.Services.GetRequiredService<SubscriptionRunner>();
        Assert.Contains("may not send", runner.Refusal(Sub()));
    }
}
