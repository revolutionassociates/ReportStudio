using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReportStudio.Core.Samples;
using ReportStudio.Core.Server;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>Signing in, roles and per-report access on the web viewer (requirement 51c).</summary>
public sealed class AccessWebTests : IDisposable
{
    private readonly ServerHarness _h;

    public AccessWebTests()
    {
        _h = new ServerHarness(access: o =>
        {
            o.Users.Add(Make("asha", "pw-asha", AccessRole.Viewer, "sales"));
            o.Users.Add(Make("ravi", "pw-ravi", AccessRole.Admin));
            o.Users.Add(Make("kiran", "pw-kiran", AccessRole.Viewer));
            o.MaxFailures = 3;
        });
        SampleReport.Create().Save(Path.Combine(_h.Dir, "other.rsr"));
    }

    public void Dispose() => _h.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessUser Make(string name, string password, AccessRole role, params string[] reports) => new()
    {
        Name = name,
        PasswordHash = PasswordHash.Create(password, PasswordHash.MinIterations),
        Role = role,
        Reports = reports.ToList()
    };

    private HttpClient As(string name, string password)
    {
        var client = _h.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + password)));
        return client;
    }

    [Fact]
    public async Task Nothing_is_shown_without_signing_in()
    {
        using var client = _h.Factory.CreateClient();
        foreach (var path in new[] { "/", "/r/sales", "/r/sales/view", "/r/sales/download/csv", "/fonts/Ubuntu-Regular.ttf" })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.StartsWith("Basic realm=\"ReportStudio\"", response.Headers.WwwAuthenticate.ToString());
            Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());   // the headers are on the refusal too
        }
    }

    [Fact]
    public async Task The_health_check_stays_open()
    {
        using var client = _h.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_name_or_password_is_refused()
    {
        using var wrongPassword = As("asha", "pw-ravi");
        using var unknown = As("nobody", "pw-asha");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongPassword.GetAsync("/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await unknown.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_viewer_sees_and_opens_only_their_reports()
    {
        using var client = As("asha", "pw-asha");

        var home = await (await client.GetAsync("/", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("href=\"/r/sales\"", home);
        Assert.DoesNotContain("/r/other", home);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/r/sales/view", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/r/sales/download/csv", Ct)).StatusCode);

        // not theirs: the same answer as for a report that does not exist
        var other = await client.GetAsync("/r/other", Ct);
        var missing = await client.GetAsync("/r/never-existed", Ct);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await other.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/r/other/view", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/r/other/download/csv", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_viewer_without_a_list_and_an_admin_see_every_report()
    {
        foreach (var (name, password) in new[] { ("kiran", "pw-kiran"), ("ravi", "pw-ravi") })
        {
            using var client = As(name, password);
            var home = await (await client.GetAsync("/", Ct)).Content.ReadAsStringAsync(Ct);
            Assert.Contains("href=\"/r/sales\"", home);
            Assert.Contains("href=\"/r/other\"", home);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/r/other/view", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Too_many_wrong_passwords_block_even_the_right_one_for_a_while()
    {
        using var guesser = As("kiran", "wrong");
        for (int i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/", Ct)).StatusCode);

        using var right = As("kiran", "pw-kiran");
        var blocked = await right.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("300", blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var other = As("asha", "pw-asha");   // other people are not affected
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_user_reaches_the_admin_interface_without_the_key()
    {
        using var admin = As("ravi", "pw-ravi");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);

        using var viewer = As("asha", "pw-asha");
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);

        using var nobody = _h.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);

        using var script = _h.Client();   // the admin key still works for scripts
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);
    }

    [Fact]
    public void A_broken_user_entry_stops_the_server_at_start()
    {
        var error = Record.Exception(() =>
        {
            using var broken = new ServerHarness(access: o => o.Users.Add(new AccessUser { Name = "x", PasswordHash = "plain text" }));
            using var client = broken.Factory.CreateClient();
        });
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Without_users_the_viewer_is_open_as_before()
    {
        using var open = new ServerHarness();
        using var client = open.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", Ct)).StatusCode);
    }
}
