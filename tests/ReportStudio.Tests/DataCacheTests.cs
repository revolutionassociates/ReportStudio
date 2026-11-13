using System.Net;
using System.Text;
using ReportStudio.Core.Data;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Data caching (requirement 41): rows reused for a time the source asks for, with the rules that keep it safe.</summary>
public sealed class DataCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-cache-" + Guid.NewGuid().ToString("N"));

    public DataCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private static List<Dictionary<string, object?>> Rows(int count) =>
        Enumerable.Range(1, count).Select(i => new Dictionary<string, object?> { ["A"] = (decimal)i, ["Name"] = "n" + i }).ToList();

    // A source that needs no file: a web address, so the key can be made without touching the disk.
    private static DataSourceDefinition Web(int seconds, string url = "https://api.example.com/x") =>
        new() { Provider = DbProvider.Rest, Source = url, CacheSeconds = seconds };

    private static Dictionary<string, object?> P(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly SourceSettings Settings = new();

    /// <summary>A stand-in for the network that counts how often it is asked.</summary>
    private sealed class Counting : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"[{""A"":1,""Name"":""Asha""}]", Encoding.UTF8, "application/json")
            });
        }
    }

    // ---- time ----

    [Fact]
    public void Rows_are_reused_until_the_time_is_up_and_then_read_again()
    {
        var now = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
        var cache = new DataCache(clock: () => now);
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(2); }

        Assert.Equal(2, cache.GetOrLoad(Web(60), P(), Settings, Load).Count);
        Assert.Equal(2, cache.GetOrLoad(Web(60), P(), Settings, Load).Count);
        Assert.Equal(1, loads);

        now = now.AddSeconds(59);
        cache.GetOrLoad(Web(60), P(), Settings, Load);
        Assert.Equal(1, loads);

        now = now.AddSeconds(2);
        cache.GetOrLoad(Web(60), P(), Settings, Load);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void A_report_cannot_ask_for_more_than_a_day()
    {
        var now = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
        var cache = new DataCache(clock: () => now);
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(1); }

        cache.GetOrLoad(Web(int.MaxValue), P(), Settings, Load);
        now = now.AddSeconds(DataCache.MaxSeconds - 1);
        cache.GetOrLoad(Web(int.MaxValue), P(), Settings, Load);
        Assert.Equal(1, loads);

        now = now.AddSeconds(2);
        cache.GetOrLoad(Web(int.MaxValue), P(), Settings, Load);
        Assert.Equal(2, loads);
    }

    [Theory]
    [InlineData(0, DbProvider.Rest)]
    [InlineData(-5, DbProvider.Rest)]
    [InlineData(60, DbProvider.Host)]
    [InlineData(60, DbProvider.Sample)]
    public void A_source_that_does_not_ask_and_Host_or_sample_rows_are_never_kept(int seconds, DbProvider provider)
    {
        var cache = new DataCache();
        var ds = Web(seconds);
        ds.Provider = provider;
        int loads = 0;

        cache.GetOrLoad(ds, P(), Settings, () => { loads++; return Rows(1); });
        cache.GetOrLoad(ds, P(), Settings, () => { loads++; return Rows(1); });

        Assert.Equal(2, loads);
        Assert.Equal(0, cache.Count);
    }

    // ---- what makes two reads the same ----

    [Fact]
    public void Different_parameter_values_are_different_reads_and_the_order_and_case_of_names_do_not_matter()
    {
        var cache = new DataCache();
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(1); }

        cache.GetOrLoad(Web(60), P(("Month", 10m), ("Dept", "Sales")), Settings, Load);
        cache.GetOrLoad(Web(60), P(("dept", "Sales"), ("MONTH", 10m)), Settings, Load);
        Assert.Equal(1, loads);

        cache.GetOrLoad(Web(60), P(("Month", 11m), ("Dept", "Sales")), Settings, Load);
        cache.GetOrLoad(Web(60), P(("Month", 10m), ("Dept", "Ops")), Settings, Load);
        cache.GetOrLoad(Web(60), P(("Month", 10m)), Settings, Load);
        Assert.Equal(4, loads);
    }

    [Fact]
    public void Lists_dates_and_missing_values_in_parameters_are_told_apart()
    {
        var cache = new DataCache();
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(1); }

        cache.GetOrLoad(Web(60), P(("Ids", new[] { 1, 2 })), Settings, Load);
        cache.GetOrLoad(Web(60), P(("Ids", new[] { 1, 2 })), Settings, Load);
        cache.GetOrLoad(Web(60), P(("Ids", new[] { 1, 3 })), Settings, Load);
        cache.GetOrLoad(Web(60), P(("Ids", new[] { 12 })), Settings, Load);
        Assert.Equal(3, loads);

        cache.GetOrLoad(Web(60), P(("From", new DateTime(2026, 11, 1))), Settings, Load);
        cache.GetOrLoad(Web(60), P(("From", new DateTime(2026, 11, 2))), Settings, Load);
        cache.GetOrLoad(Web(60), P(("From", null)), Settings, Load);
        cache.GetOrLoad(Web(60), P(("From", "")), Settings, Load);
        Assert.Equal(7, loads);
    }

    [Fact]
    public void A_different_address_selector_or_header_is_a_different_read()
    {
        var cache = new DataCache();
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(1); }

        cache.GetOrLoad(Web(60), P(), Settings, Load);
        cache.GetOrLoad(Web(60, "https://api.example.com/y"), P(), Settings, Load);
        var selected = Web(60); selected.Selector = "data.items";
        cache.GetOrLoad(selected, P(), Settings, Load);
        var keyed = Web(60); keyed.Headers = "X-Api-Key: k2";
        cache.GetOrLoad(keyed, P(), Settings, Load);

        Assert.Equal(4, loads);
    }

    [Fact]
    public void What_a_run_may_read_is_part_of_the_key()
    {
        var cache = new DataCache();
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(1); }

        cache.GetOrLoad(Web(60), P(), Settings, Load);
        cache.GetOrLoad(Web(60), P(), new SourceSettings { Access = new SourceAccess { AllowPrivateAddresses = true } }, Load);
        cache.GetOrLoad(Web(60), P(), new SourceSettings { FileRoot = _dir }, Load);

        Assert.Equal(3, loads);
    }

    // ---- care ----

    [Fact]
    public void Callers_get_copies_so_changing_the_rows_of_one_run_never_changes_the_next()
    {
        var cache = new DataCache();

        var first = cache.GetOrLoad(Web(60), P(), Settings, () => Rows(2));
        first[0]["A"] = 99m;
        first.RemoveAt(1);

        var second = cache.GetOrLoad(Web(60), P(), Settings, () => Rows(5));
        Assert.Equal(2, second.Count);
        Assert.Equal(1m, second[0]["A"]);
        second[0]["Name"] = "changed";

        var third = cache.GetOrLoad(Web(60), P(), Settings, () => Rows(5));
        Assert.Equal("n1", third[0]["Name"]);
    }

    [Fact]
    public void An_error_is_never_kept()
    {
        var cache = new DataCache();
        int loads = 0;

        Assert.Throws<InvalidOperationException>(() => cache.GetOrLoad(Web(60), P(), Settings, () => { loads++; throw new InvalidOperationException("down"); }));
        var rows = cache.GetOrLoad(Web(60), P(), Settings, () => { loads++; return Rows(1); });

        Assert.Single(rows);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void The_least_recently_used_read_goes_first_when_there_are_too_many()
    {
        var cache = new DataCache(maxEntries: 2);
        var loads = new Dictionary<string, int>();
        List<Dictionary<string, object?>> Read(string key)
        {
            return cache.GetOrLoad(Web(60), P(("K", key)), Settings, () => { loads[key] = loads.GetValueOrDefault(key) + 1; return Rows(1); });
        }

        Read("a"); Read("b");
        Read("a");                 // a is used again, so b is the one to go
        Read("c");
        Assert.Equal(2, cache.Count);

        Read("a"); Read("c");
        Assert.Equal(1, loads["a"]);
        Assert.Equal(1, loads["c"]);
        Read("b");
        Assert.Equal(2, loads["b"]);
    }

    [Fact]
    public void Too_many_rows_push_out_the_oldest_and_one_read_that_is_too_big_is_not_kept()
    {
        var cache = new DataCache(maxRows: 5);
        int loads = 0;
        List<Dictionary<string, object?>> Read(string key, int count) =>
            cache.GetOrLoad(Web(60), P(("K", key)), Settings, () => { loads++; return Rows(count); });

        Read("a", 3); Read("b", 3);
        Assert.Equal(1, cache.Count);              // 6 rows are more than 5: a went
        Assert.Equal(3, cache.RowCount);

        Assert.Equal(6, Read("huge", 6).Count);
        Assert.Equal(1, cache.Count);              // not kept
        Read("huge", 6);
        Assert.Equal(4, loads);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var cache = new DataCache();
        int loads = 0;
        List<Dictionary<string, object?>> Load() { loads++; return Rows(1); }

        cache.GetOrLoad(Web(60), P(), Settings, Load);
        cache.Clear();
        cache.GetOrLoad(Web(60), P(), Settings, Load);

        Assert.Equal(2, loads);
    }

    // ---- through the loader ----

    [Fact]
    public void A_file_is_reused_until_it_changes()
    {
        var path = Path.Combine(_dir, "p.csv");
        File.WriteAllText(path, "Name\nAsha\n");
        var written = File.GetLastWriteTimeUtc(path);
        var settings = new SourceSettings { BaseDirectory = _dir, Cache = new DataCache() };
        var ds = new DataSourceDefinition { Provider = DbProvider.Csv, Source = "p.csv", CacheSeconds = 60 };

        Assert.Equal("Asha", Assert.Single(DataLoader.Load(ds, P(), settings))["Name"]);

        // Same size, same time stamp: the file looks unchanged, so the kept rows are used.
        File.WriteAllText(path, "Name\nAnya\n");
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal("Asha", Assert.Single(DataLoader.Load(ds, P(), settings))["Name"]);

        // A different size is a changed file: read again.
        File.WriteAllText(path, "Name\nAnya\nRavi\n");
        Assert.Equal(2, DataLoader.Load(ds, P(), settings).Count);
    }

    [Fact]
    public void A_file_that_is_not_there_gives_the_loaders_message_and_keeps_nothing()
    {
        var cache = new DataCache();
        var settings = new SourceSettings { BaseDirectory = _dir, Cache = cache };
        var ds = new DataSourceDefinition { Provider = DbProvider.Csv, Source = "missing.csv", CacheSeconds = 60 };

        Assert.Throws<SourceException>(() => DataLoader.Load(ds, P(), settings));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_web_address_is_asked_once_for_the_same_parameters_and_again_for_other_ones()
    {
        var net = new Counting();
        var settings = new SourceSettings { Cache = new DataCache(), Access = new SourceAccess { HttpHandler = net } };
        var ds = Web(60, "https://api.example.com/people?city={City}");

        DataLoader.Load(ds, P(("City", "Pune")), settings);
        DataLoader.Load(ds, P(("City", "Pune")), settings);
        Assert.Equal(1, net.Calls);

        DataLoader.Load(ds, P(("City", "Delhi")), settings);
        Assert.Equal(2, net.Calls);
    }

    [Fact]
    public void A_run_that_may_not_read_the_web_is_not_handed_rows_another_run_read()
    {
        var net = new Counting();
        var cache = new DataCache();
        var ds = Web(60);

        DataLoader.Load(ds, P(), new SourceSettings { Cache = cache, Access = new SourceAccess { HttpHandler = net } });
        Assert.Equal(1, cache.Count);

        var off = new SourceSettings { Cache = cache, Access = new SourceAccess { HttpHandler = net, AllowWebSources = false } };
        var ex = Assert.Throws<SourceException>(() => DataLoader.Load(ds, P(), off));

        Assert.Equal(SourceAccess.WebOffMessage, ex.Message);
    }

    [Fact]
    public void Without_a_cache_nothing_is_kept_even_when_the_source_asks()
    {
        var net = new Counting();
        var settings = new SourceSettings { Access = new SourceAccess { HttpHandler = net } };

        DataLoader.Load(Web(60), P(), settings);
        DataLoader.Load(Web(60), P(), settings);

        Assert.Equal(2, net.Calls);
    }

    // ---- in a report ----

    private static ReportDefinition WebReport(int seconds)
    {
        var def = new ReportDefinition { Name = "Cached" };
        def.DataSource = Web(seconds);
        return def;
    }

    [Fact]
    public void Two_runs_that_share_a_cache_read_the_source_once()
    {
        var net = new Counting();
        var cache = new DataCache();
        ReportOptions Options() => new() { Cache = cache, Access = new SourceAccess { HttpHandler = net } };

        Assert.Equal(1, ReportEngine.Run(WebReport(60), null, Options()).RowCount);
        Assert.Equal(1, ReportEngine.Run(WebReport(60), null, Options()).RowCount);

        Assert.Equal(1, net.Calls);
    }

    [Fact]
    public void Runs_without_a_shared_cache_or_with_a_source_that_does_not_ask_read_every_time()
    {
        var net = new Counting();
        ReportEngine.Run(WebReport(60), null, new ReportOptions { Access = new SourceAccess { HttpHandler = net } });
        ReportEngine.Run(WebReport(60), null, new ReportOptions { Access = new SourceAccess { HttpHandler = net } });
        Assert.Equal(2, net.Calls);

        var shared = new DataCache();
        ReportEngine.Run(WebReport(0), null, new ReportOptions { Cache = shared, Access = new SourceAccess { HttpHandler = net } });
        ReportEngine.Run(WebReport(0), null, new ReportOptions { Cache = shared, Access = new SourceAccess { HttpHandler = net } });
        Assert.Equal(4, net.Calls);
    }

    // ---- the definition ----

    [Fact]
    public void The_time_is_saved_with_the_report_and_is_off_by_default()
    {
        Assert.Equal(0, new DataSourceDefinition().CacheSeconds);

        var def = new ReportDefinition { DataSource = Web(90) };
        var back = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal(90, back.DataSource.CacheSeconds);
    }
}
