using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportStudio.Core.Samples;

namespace ReportStudio.Web.Tests;

/// <summary>Mail that is only written down, so no test sends anything.</summary>
internal sealed class FakeMail : IMailSender
{
    private readonly List<MailRequest> _sent = new();
    public Exception? Fail { get; set; }

    public IReadOnlyList<MailRequest> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(MailRequest mail, CancellationToken ct)
    {
        if (Fail != null) throw Fail;
        lock (_sent) _sent.Add(mail);
        return Task.CompletedTask;
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A web host with the report server switched on, one report ("sales"), fake mail and a clock the test moves.</summary>
internal sealed class ServerHarness : IDisposable
{
    public const string Key = "test-admin-key-0123456789";

    public string Dir { get; } = TestFiles.NewFolder();
    public string SubscriptionsFile => Path.Combine(Dir, "subscriptions.json");
    public FakeMail Mail { get; } = new();
    public TestClock Clock { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public ServerHarness(Action<ServerOptions>? tweak = null, Action<AccessOptions>? access = null)
    {
        SampleReport.Create().Save(Path.Combine(Dir, "sales.rsr"));

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<WebViewerOptions>(o => o.ReportsFolder = Dir);
                services.Configure<ServerOptions>(o =>
                {
                    o.Enabled = true;
                    o.SubscriptionsFile = SubscriptionsFile;
                    o.PollSeconds = 3600;   // the tests run due subscriptions themselves
                    o.AdminKey = Key;
                    o.AllowedRecipientDomains.Add("example.com");
                    o.Smtp.Host = "smtp.invalid";
                    o.Smtp.From = "reports@example.com";
                    tweak?.Invoke(o);
                });
                services.Configure<AccessOptions>(o => access?.Invoke(o));
                services.RemoveAll<IMailSender>();
                services.AddSingleton<IMailSender>(Mail);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            }));
    }

    public HttpClient Client(bool withKey = true)
    {
        var client = Factory.CreateClient();
        if (withKey) client.DefaultRequestHeaders.Add(AdminApi.KeyHeader, Key);
        return client;
    }

    public void Dispose()
    {
        Factory.Dispose();
        TestFiles.Delete(Dir);
    }
}
