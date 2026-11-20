using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>The administrator's JSON interface: closed unless a key is set, and strict about what it stores (requirement 50).</summary>
public sealed class AdminApiTests : IDisposable
{
    private readonly ServerHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Valid = """
        { "name": "Daily sales", "reportId": "sales", "format": "csv", "recipients": ["a@example.com"],
          "schedule": { "kind": "Interval", "intervalMinutes": 60, "timeZone": "UTC" } }
        """;

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private async Task<string> AddOne(HttpClient client)
    {
        var response = await client.PostAsync("/admin/api/subscriptions", Json(Valid), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Body(response)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Without_a_key_in_the_settings_the_api_is_not_there()
    {
        using var closed = new ServerHarness(o => o.AdminKey = "");
        using var client = closed.Client();   // sends a key, but none is set on the server
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_key_that_is_too_short_does_not_open_the_api()
    {
        using var weak = new ServerHarness(o => o.AdminKey = "short");
        using var client = weak.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApi.KeyHeader, "short");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);
    }

    [Fact]
    public async Task When_the_server_is_off_the_api_is_not_there()
    {
        using var off = new ServerHarness(o => o.Enabled = false);
        using var client = off.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_missing_or_wrong_key_is_refused()
    {
        using var none = _h.Client(withKey: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await none.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);

        using var wrong = _h.Factory.CreateClient();
        wrong.DefaultRequestHeaders.Add(AdminApi.KeyHeader, "not-the-key-not-the-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/admin/api/subscriptions", Ct)).StatusCode);
    }

    [Fact]
    public async Task Answers_are_never_cached_and_carry_the_security_headers()
    {
        using var client = _h.Client();
        var response = await client.GetAsync("/admin/api/subscriptions", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task A_subscription_can_be_added_read_changed_and_removed()
    {
        using var client = _h.Client();
        var id = await AddOne(client);

        var list = await Body(await client.GetAsync("/admin/api/subscriptions", Ct));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Interval", list[0].GetProperty("schedule").GetProperty("kind").GetString());
        Assert.Equal("Every 60 minutes", list[0].GetProperty("scheduleText").GetString());

        var changed = Valid.Replace("Daily sales", "Renamed");
        var put = await client.PutAsync("/admin/api/subscriptions/" + id, Json(changed), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Renamed", (await Body(await client.GetAsync("/admin/api/subscriptions/" + id, Ct))).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/admin/api/subscriptions/" + id, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/admin/api/subscriptions/" + id, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/admin/api/subscriptions/" + id, Json(changed), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("a@example.com", "x@other.org", "may not send")]
    [InlineData("a@example.com", "not an address", "not a valid e-mail")]
    public async Task Recipients_are_checked_against_the_allowed_domains(string first, string second, string expected)
    {
        using var client = _h.Client();
        var body = Valid.Replace("\"a@example.com\"", $"\"{first}\", \"{second}\"");
        var response = await client.PostAsync("/admin/api/subscriptions", Json(body), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await Body(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("\"format\": \"csv\"", "\"format\": \"pdf\"", "not offered")]
    [InlineData("\"reportId\": \"sales\"", "\"reportId\": \"../secret\"", "not allowed")]
    [InlineData("\"intervalMinutes\": 60", "\"intervalMinutes\": 1", "interval must be")]
    public async Task Other_mistakes_are_refused_with_a_sentence(string from, string to, string expected)
    {
        using var client = _h.Client();
        var response = await client.PostAsync("/admin/api/subscriptions", Json(Valid.Replace(from, to)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await Body(response)).GetProperty("error").GetString());
        Assert.Equal(0, (await Body(await client.GetAsync("/admin/api/subscriptions", Ct))).GetArrayLength());
    }

    [Fact]
    public async Task A_report_that_is_not_in_the_folder_can_be_stored_but_fails_when_it_runs()
    {
        // The folder is the server owner's and may change after the subscription is made, so the check is made at run time (see the scheduler tests).
        using var client = _h.Client();
        var response = await client.PostAsync("/admin/api/subscriptions", Json(Valid.Replace("sales", "later")), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_request_that_is_too_big_is_refused()
    {
        using var client = _h.Client();
        var big = Valid.Replace("Daily sales", new string('x', 70 * 1024));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/admin/api/subscriptions", Json(big), Ct)).StatusCode);
    }

    [Fact]
    public async Task Run_now_mails_the_report_and_records_the_result()
    {
        using var client = _h.Client();
        var id = await AddOne(client);

        var run = await client.PostAsync($"/admin/api/subscriptions/{id}/run", null, Ct);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.True((await Body(run)).GetProperty("ok").GetBoolean());
        Assert.Single(_h.Mail.Sent);
        Assert.Equal("ok", (await Body(await client.GetAsync("/admin/api/subscriptions/" + id, Ct))).GetProperty("lastStatus").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/api/subscriptions/missing/run", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Run_now_reports_a_failure_with_status_422()
    {
        _h.Mail.Fail = new IOException("nope");
        using var client = _h.Client();
        var id = await AddOne(client);
        var run = await client.PostAsync($"/admin/api/subscriptions/{id}/run", null, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, run.StatusCode);
        Assert.False((await Body(run)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void KeyMatches_compares_whole_keys()
    {
        Assert.True(AdminApi.KeyMatches("abc", "abc"));
        Assert.False(AdminApi.KeyMatches("abc", "abd"));
        Assert.False(AdminApi.KeyMatches("abc", "abcd"));
        Assert.False(AdminApi.KeyMatches("", ""));
        Assert.False(AdminApi.KeyMatches("abc", ""));
    }

    [Fact]
    public async Task The_viewers_pages_do_not_show_subscriptions()
    {
        using var client = _h.Client(withKey: false);
        var html = await (await client.GetAsync("/", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("subscription", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", html, StringComparison.OrdinalIgnoreCase);
    }
}
