using System.Net;
using System.Text;
using ReportStudio.Core.Data;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>REST and OData sources (requirement 40, second part), and the switches that turn file and web sources off. No real network is used.</summary>
public sealed class WebSourceTests
{
    private sealed record Seen(Uri Uri, Dictionary<string, string> Headers);

    /// <summary>A stand-in for the network: answers each request with the function's response and remembers what was asked.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public List<Seen> Requests { get; } = new();

        public Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = (r, _) => Task.FromResult(respond(r));
        public Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static SourceSettings Settings(Stub stub, bool privateAddresses = false) => new()
    {
        Access = new SourceAccess { HttpHandler = stub, AllowPrivateAddresses = privateAddresses, WebTimeout = TimeSpan.FromSeconds(5) }
    };

    private static DataSourceDefinition Rest(string url, string selector = "", string headers = "") =>
        new() { Provider = DbProvider.Rest, Source = url, Selector = selector, Headers = headers };

    private static DataSourceDefinition OData(string url) => new() { Provider = DbProvider.OData, Source = url };

    private static readonly Dictionary<string, object?> NoParameters = new();

    // ---- REST ----

    [Fact]
    public void Rest_reads_an_array_of_objects_and_sends_the_headers()
    {
        var stub = new Stub(_ => Json(@"[{""Name"":""Asha"",""Qty"":2},{""Name"":""Ravi"",""Qty"":3}]"));

        var rows = DataLoader.Load(Rest("https://api.example.com/people", headers: "X-Api-Key: k123\nAccept-Language: en"), NoParameters, Settings(stub));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Asha", rows[0]["Name"]);
        Assert.Equal(3m, rows[1]["Qty"]);
        var seen = Assert.Single(stub.Requests);
        Assert.Equal("k123", seen.Headers["X-Api-Key"]);
        Assert.Equal("en", seen.Headers["Accept-Language"]);
        Assert.Contains("json", seen.Headers["Accept"]);
    }

    [Fact]
    public void Rest_finds_the_rows_by_a_path()
    {
        var stub = new Stub(_ => Json(@"{""data"":{""items"":[{""id"":1},{""id"":2}]}}"));

        var rows = DataLoader.Load(Rest("https://api.example.com/x", "data.items"), NoParameters, Settings(stub));

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Parameters_go_into_the_address_written_so_they_cannot_change_its_shape()
    {
        var stub = new Stub(_ => Json("[]"));
        var parameters = new Dictionary<string, object?>
        {
            ["City"] = "New Delhi&admin=1/../x", ["From"] = new DateTime(2026, 11, 9), ["Count"] = 5m, ["Active"] = true, ["Nothing"] = null
        };

        DataLoader.Load(Rest("https://api.example.com/q?city={city}&from={From}&n={Count}&a={Active}&z={Nothing}"), parameters, Settings(stub));

        Assert.Equal("https://api.example.com/q?city=New%20Delhi%26admin%3D1%2F..%2Fx&from=2026-11-09&n=5&a=true&z=",
            Assert.Single(stub.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public void An_address_that_names_a_parameter_the_report_does_not_have_says_so()
    {
        var ex = Assert.Throws<SourceException>(() => WebSourceLoader.BuildAddress("https://a.example.com/{Nope}", NoParameters));

        Assert.Contains("Nope", ex.Message);
    }

    // ---- OData ----

    [Fact]
    public void OData_reads_the_value_list_and_follows_the_next_page_links()
    {
        var stub = new Stub(r => r.RequestUri!.AbsoluteUri.EndsWith("skip=2")
            ? Json(@"{""value"":[{""Id"":3}]}")
            : Json(@"{""value"":[{""Id"":1},{""Id"":2}],""@odata.nextLink"":""https://api.example.com/odata/People?skip=2""}"));

        var rows = DataLoader.Load(OData("https://api.example.com/odata/People"), NoParameters, Settings(stub));

        Assert.Equal(new object?[] { 1m, 2m, 3m }, rows.Select(r => r["Id"]).ToArray());
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public void OData_follows_a_relative_next_link_and_a_single_entity_is_one_row()
    {
        var calls = 0;
        var stub = new Stub(_ => ++calls == 1
            ? Json(@"{""value"":[{""Id"":1}],""@odata.nextLink"":""People?page=2""}")
            : Json(@"{""value"":[{""Id"":2}]}"));

        Assert.Equal(2, DataLoader.Load(OData("https://api.example.com/odata/People"), NoParameters, Settings(stub)).Count);

        var one = new Stub(_ => Json(@"{""Id"":7,""Name"":""Asha""}"));
        var row = Assert.Single(DataLoader.Load(OData("https://api.example.com/odata/People(7)"), NoParameters, Settings(one)));
        Assert.Equal(7m, row["Id"]);
    }

    [Fact]
    public void OData_does_not_follow_a_next_link_to_another_server_and_stops_after_the_page_limit()
    {
        var elsewhere = new Stub(_ => Json(@"{""value"":[{""Id"":1}],""@odata.nextLink"":""https://evil.example.net/steal""}"));
        Assert.Throws<SourceException>(() => DataLoader.Load(OData("https://api.example.com/odata/P"), NoParameters, Settings(elsewhere)));
        Assert.Single(elsewhere.Requests);

        var endless = new Stub(_ => Json(@"{""value"":[{""Id"":1}],""@odata.nextLink"":""https://api.example.com/odata/P?next""}"));
        var ex = Assert.Throws<SourceException>(() => DataLoader.Load(OData("https://api.example.com/odata/P"), NoParameters, Settings(endless)));
        Assert.Contains(WebSourceLoader.MaxPages.ToString(), ex.Message);
        Assert.Equal(WebSourceLoader.MaxPages, endless.Requests.Count);
    }

    [Fact]
    public void Testing_and_detecting_fields_read_only_the_first_page()
    {
        var stub = new Stub(_ => Json(@"{""value"":[{""A"":1,""B"":2}],""@odata.nextLink"":""https://api.example.com/odata/P?next""}"));
        var ds = OData("https://api.example.com/odata/P");

        DataLoader.TestConnection(ds, Settings(stub));
        Assert.Equal(new[] { "A", "B" }, DataLoader.DetectFieldNames(ds, Settings(stub)));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public void Listing_fields_never_uses_the_network_but_gives_the_saved_fields()
    {
        var stub = new Stub(_ => throw new InvalidOperationException("the network must not be used"));
        var ds = Rest("https://api.example.com/x");
        ds.Fields.Add(new FieldDefinition { Name = "Saved" });

        Assert.Equal(new[] { "Saved" }, DataLoader.GetFieldNames(ds, Settings(stub)));
        Assert.Empty(stub.Requests);
    }

    // ---- redirects, errors, limits ----

    [Fact]
    public void A_redirect_on_the_same_server_keeps_the_headers_and_one_to_another_server_drops_them()
    {
        var same = new Stub(r => r.RequestUri!.AbsolutePath == "/old" ? Redirect("https://api.example.com/new") : Json("[]"));
        DataLoader.Load(Rest("https://api.example.com/old", headers: "X-Api-Key: k123"), NoParameters, Settings(same));
        Assert.Equal("k123", same.Requests[1].Headers["X-Api-Key"]);

        var other = new Stub(r => r.RequestUri!.Host == "api.example.com" ? Redirect("https://cdn.example.net/data") : Json("[]"));
        DataLoader.Load(Rest("https://api.example.com/old", headers: "X-Api-Key: k123"), NoParameters, Settings(other));
        Assert.Equal("cdn.example.net", other.Requests[1].Uri.Host);
        Assert.False(other.Requests[1].Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public void A_redirect_to_a_plain_http_or_local_address_or_too_many_redirects_are_refused()
    {
        var downgrade = new Stub(_ => Redirect("http://api.example.com/x"));
        Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, Settings(downgrade)));

        var inside = new Stub(_ => Redirect("https://169.254.169.254/latest/meta-data"));
        Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, Settings(inside)));
        Assert.Single(inside.Requests);

        var loop = new Stub(_ => Redirect("https://api.example.com/again"));
        Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, Settings(loop)));
        Assert.Equal(WebSourceLoader.MaxRedirects + 1, loop.Requests.Count);
    }

    [Theory]
    [InlineData(404, "Not Found")]
    [InlineData(500, "Internal Server Error")]
    public void A_failed_answer_names_the_status_but_never_the_address_or_the_key(int code, string reason)
    {
        var stub = new Stub(_ => new HttpResponseMessage((HttpStatusCode)code) { ReasonPhrase = reason });
        var ds = Rest("https://api.example.com/x?apikey=SECRET123", headers: "Authorization: Bearer TOKEN456");

        var ex = Assert.Throws<SourceException>(() => DataLoader.Load(ds, NoParameters, Settings(stub)));

        Assert.Contains(code.ToString(), ex.Message);
        Assert.DoesNotContain("SECRET123", ex.Message);
        Assert.DoesNotContain("TOKEN456", ex.Message);
        Assert.DoesNotContain("api.example.com", ex.Message);
    }

    [Fact]
    public void An_answer_that_is_not_json_or_is_too_big_or_too_slow_is_refused()
    {
        var html = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, Settings(html)));

        var big = new Stub(_ =>
        {
            var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentLength = SourceLimits.MaxBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, Settings(big)));

        var slow = new Stub(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json("[]"); });
        var settings = new SourceSettings { Access = new SourceAccess { HttpHandler = slow, WebTimeout = TimeSpan.FromMilliseconds(300) } };
        var ex = Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, settings));
        Assert.Contains("did not answer", ex.Message);
    }

    // ---- which addresses are allowed ----

    [Theory]
    [InlineData("http://api.example.com/x")]
    [InlineData("ftp://api.example.com/x")]
    [InlineData("https://localhost/x")]
    [InlineData("https://app.localhost/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://192.168.1.10:8443/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://[fe80::1]/x")]
    [InlineData("https://user:pass@api.example.com/x")]
    [InlineData("people.json")]
    public void An_address_that_is_not_https_or_is_inside_or_carries_a_password_is_refused_without_a_request(string address)
    {
        var stub = new Stub(_ => Json("[]"));

        Assert.Throws<SourceException>(() => DataLoader.Load(Rest(address), NoParameters, Settings(stub)));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void With_private_addresses_allowed_the_local_network_and_plain_http_work()
    {
        var stub = new Stub(_ => Json(@"[{""a"":1}]"));

        Assert.Single(DataLoader.Load(Rest("http://10.0.0.5:8080/api"), NoParameters, Settings(stub, privateAddresses: true)));
        Assert.Single(DataLoader.Load(Rest("https://localhost:5001/api"), NoParameters, Settings(stub, privateAddresses: true)));
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("198.18.0.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("2002:0a00:0001::1", true)]
    [InlineData("64:ff9b::7f00:1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Addresses_inside_the_network_are_told_from_public_ones(string address, bool isPrivate)
    {
        Assert.Equal(isPrivate, WebSourceLoader.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public void Detecting_the_fields_of_a_web_address_reads_the_first_page_and_gives_the_types()
    {
        var stub = new Stub(_ => Json(@"{""value"":[{""Id"":1,""Name"":""Asha"",""Joined"":""2026-11-09""}],""@odata.nextLink"":""https://api.example.com/odata/P?next""}"));

        var fields = DataLoader.DetectFields(OData("https://api.example.com/odata/P"), Settings(stub));

        Assert.Equal(new[] { "Id:Number", "Name:Text", "Joined:Date" }, fields.Select(f => f.ToString()).ToArray());
        Assert.Single(stub.Requests);
    }

    // ---- headers ----

    [Fact]
    public void Headers_are_read_one_per_line_and_the_ones_that_may_not_be_set_are_refused()
    {
        var headers = WebSourceLoader.ParseHeaders("X-Api-Key: a:b\r\n\r\n  Accept-Language :  en  \n");

        Assert.Equal(new[] { ("X-Api-Key", "a:b"), ("Accept-Language", "en") }, headers.ToArray());
        Assert.Empty(WebSourceLoader.ParseHeaders(""));
        Assert.Throws<SourceException>(() => WebSourceLoader.ParseHeaders("Host: evil.example.com"));
        Assert.Throws<SourceException>(() => WebSourceLoader.ParseHeaders("no colon here"));
        Assert.Throws<SourceException>(() => WebSourceLoader.ParseHeaders("Bad Name: x"));
    }

    // ---- the switches ----

    [Fact]
    public void With_web_sources_off_nothing_is_asked_of_the_network()
    {
        var stub = new Stub(_ => Json("[]"));
        var settings = new SourceSettings { Access = new SourceAccess { AllowWebSources = false, HttpHandler = stub } };

        var ex = Assert.Throws<SourceException>(() => DataLoader.Load(Rest("https://api.example.com/x"), NoParameters, settings));

        Assert.Equal(SourceAccess.WebOffMessage, ex.Message);
        Assert.Empty(stub.Requests);
        Assert.Throws<SourceException>(() => DataLoader.TestConnection(Rest("https://api.example.com/x"), settings));
    }

    [Fact]
    public void With_file_sources_off_no_file_is_read_and_the_saved_fields_are_listed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.csv"), "Name\nAsha\n");
            var settings = new SourceSettings { BaseDirectory = dir, Access = new SourceAccess { AllowFileSources = false } };
            var ds = new DataSourceDefinition { Provider = DbProvider.Csv, Source = "a.csv" };
            ds.Fields.Add(new FieldDefinition { Name = "Saved" });

            var ex = Assert.Throws<SourceException>(() => DataLoader.Load(ds, NoParameters, settings));
            Assert.Equal(SourceAccess.FilesOffMessage, ex.Message);
            Assert.Throws<SourceException>(() => DataLoader.TestConnection(ds, settings));
            Assert.Equal(new[] { "Saved" }, DataLoader.GetFieldNames(ds, settings));
            Assert.Single(DataLoader.Load(ds, NoParameters, new SourceSettings { BaseDirectory = dir }));     // allowed by default
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- in a report ----

    private static ReportDefinition WebReport(string url)
    {
        var def = new ReportDefinition { Name = "Web" };
        def.DataSource.Provider = DbProvider.Rest;
        def.DataSource.Source = url;
        return def;
    }

    [Fact]
    public void A_report_runs_on_the_rows_of_a_web_address()
    {
        var stub = new Stub(_ => Json(@"[{""Name"":""Asha""},{""Name"":""Ravi""},{""Name"":""Meena""}]"));

        var result = ReportEngine.Run(WebReport("https://api.example.com/people"), null, new ReportOptions { Access = new SourceAccess { HttpHandler = stub } });

        Assert.Equal(3, result.RowCount);
    }

    [Fact]
    public void A_report_run_with_web_sources_off_stops_with_a_message_and_asks_nothing()
    {
        var stub = new Stub(_ => Json("[]"));
        var options = new ReportOptions { Access = new SourceAccess { AllowWebSources = false, HttpHandler = stub } };

        var ex = Assert.Throws<SourceException>(() => ReportEngine.Run(WebReport("https://api.example.com/people"), null, options));

        Assert.Equal(SourceAccess.WebOffMessage, ex.Message);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void A_data_set_from_a_web_address_is_refused_with_the_rest_when_web_sources_are_off()
    {
        var def = new ReportDefinition { Name = "Sets" };
        def.DataSource.Provider = DbProvider.Host;
        def.DataSets.Add(new DataSetDefinition { Name = "Prices", DataSource = Rest("https://api.example.com/prices") });
        var stub = new Stub(_ => Json("[]"));
        var options = new ReportOptions
        {
            Rows = new List<Dictionary<string, object?>> { new() { ["A"] = 1 } },
            Access = new SourceAccess { AllowWebSources = false, HttpHandler = stub }
        };

        Assert.Throws<SourceException>(() => ReportEngine.Run(def, null, options));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void A_host_only_report_makes_every_data_set_a_host_data_set_so_nothing_is_read()
    {
        var def = new ReportDefinition { Name = "Sets" };
        def.DataSource = Rest("https://api.example.com/rows");
        def.DataSets.Add(new DataSetDefinition { Name = "Prices", DataSource = Rest("https://api.example.com/prices", headers: "X-Api-Key: k") });
        def.DataSets.Add(new DataSetDefinition { Name = "Staff", DataSource = new DataSourceDefinition { Provider = DbProvider.Csv, Source = "staff.csv" } });
        var stub = new Stub(_ => Json("[]"));

        def.UseHostDataOnly();

        Assert.All(def.DataSets, d => Assert.Equal(DbProvider.Host, d.DataSource.Provider));
        Assert.All(def.DataSets, d => Assert.Equal("", d.DataSource.Source));
        Assert.False(def.HasOwnQuery);
        var options = new ReportOptions
        {
            Rows = new List<Dictionary<string, object?>> { new() { ["A"] = 1 } },
            Access = new SourceAccess { HttpHandler = stub }
        };
        Assert.Equal(1, ReportEngine.Run(def, null, options).RowCount);
        Assert.Empty(stub.Requests);
    }

    // ---- the definition ----

    [Fact]
    public void A_report_says_which_kinds_of_outside_data_it_reads()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = "a.csv";
        Assert.True(def.UsesFileSources);
        Assert.False(def.UsesWebSources);

        def.DataSets.Add(new DataSetDefinition { Name = "W", DataSource = Rest("https://api.example.com/x") });
        Assert.True(def.UsesWebSources);

        var empty = new ReportDefinition();
        empty.DataSource.Provider = DbProvider.Rest;
        Assert.False(empty.UsesWebSources);        // no address, nothing is read
    }

    [Fact]
    public void The_trust_text_lists_the_names_of_the_headers_and_never_their_values()
    {
        var ds = Rest("https://api.example.com/x?apikey=SECRET1", headers: "X-Api-Key: SECRET2\nAuthorization: Bearer SECRET3");

        var text = ds.DescribeForTrust(600);

        Assert.Contains("X-Api-Key", text);
        Assert.Contains("Authorization", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public void Headers_are_saved_with_the_report_and_a_Host_report_drops_them()
    {
        var def = new ReportDefinition();
        def.DataSource = Rest("https://api.example.com/x", headers: "X-Api-Key: k");
        Assert.Equal("X-Api-Key: k", ReportDefinition.FromJson(def.ToJson()).DataSource.Headers);

        def.DataSource.Provider = DbProvider.Host;
        def.Normalize();
        Assert.Equal("", def.DataSource.Headers);
        Assert.Equal("", def.DataSource.Source);
    }
}
