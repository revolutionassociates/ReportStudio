using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data;

using Row = Dictionary<string, object?>;

/// <summary>
/// Keeps the rows of a data source for a while, so a report run again (new page, another export, the same price list used by several
/// reports) does not read the database, file or web address again. A source asks for it with <see cref="DataSourceDefinition.CacheSeconds"/>;
/// the host or viewer owns the cache and passes it in <c>ReportOptions.Cache</c>. Without one, nothing is kept.
/// </summary>
/// <remarks>
/// What makes two reads "the same": the provider, connection, query, file or address, selector, separator, header settings, every report
/// parameter, the base folder, the access switches, and for a file its size and last-changed time (an edited file is read again).
/// The key is a hash, so connection strings and keys are not kept in memory as text. Callers get copies: changing the rows of a run
/// never changes what the next run gets. An error is never kept.
/// </remarks>
public sealed class DataCache
{
    /// <summary>The longest time rows are kept (one day), whatever a report file asks for.</summary>
    public const int MaxSeconds = 86400;

    private sealed class Entry
    {
        public required List<Row> Rows { get; init; }
        public required DateTime Expires { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly int _maxEntries;
    private readonly long _maxRows;
    private readonly Func<DateTime> _clock;
    private long _tick;

    /// <param name="maxEntries">How many different reads are kept at most (the least recently used go first).</param>
    /// <param name="maxRows">How many rows are kept in all; a single read with more rows than this is not kept at all.</param>
    /// <param name="clock">Time in UTC; replaced in tests.</param>
    public DataCache(int maxEntries = 32, long maxRows = 2_000_000, Func<DateTime>? clock = null)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxRows = Math.Max(1, maxRows);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    public long RowCount { get { lock (_lock) return _entries.Values.Sum(e => (long)e.Rows.Count); } }

    public void Clear() { lock (_lock) _entries.Clear(); }

    /// <summary>
    /// The rows from the cache when they are still fresh, otherwise from <paramref name="load"/> (and kept when the source asks for it).
    /// Sources that do not ask (CacheSeconds 0), the sample data and Host rows are always loaded.
    /// </summary>
    public List<Row> GetOrLoad(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings, Func<List<Row>> load)
    {
        int seconds = Math.Clamp(ds.CacheSeconds, 0, MaxSeconds);
        if (seconds == 0 || ds.Provider is DbProvider.Sample or DbProvider.Host) return load();

        string key;
        try { key = KeyOf(ds, parameters, settings); }
        catch (Exception ex) when (ex is SourceException or IOException or UnauthorizedAccessException)
        {
            return load();       // a file that cannot be found and the like: the loader gives the real message
        }

        var now = _clock();
        lock (_lock)
            if (_entries.TryGetValue(key, out var hit))
            {
                if (hit.Expires > now) { hit.LastUsed = ++_tick; return Copy(hit.Rows); }
                _entries.Remove(key);
            }

        var rows = load();

        if (rows.Count <= _maxRows)
            lock (_lock)
            {
                foreach (var stale in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList()) _entries.Remove(stale);
                _entries[key] = new Entry { Rows = Copy(rows), Expires = now.AddSeconds(seconds), LastUsed = ++_tick };
                Evict(key);
            }
        return rows;
    }

    // Removes the least recently used reads until the limits hold; the read just stored stays.
    private void Evict(string keep)
    {
        while (_entries.Count > _maxEntries || _entries.Values.Sum(e => (long)e.Rows.Count) > _maxRows)
        {
            var oldest = _entries.Where(e => e.Key != keep).OrderBy(e => e.Value.LastUsed).Select(e => e.Key).FirstOrDefault();
            if (oldest == null) break;
            _entries.Remove(oldest);
        }
    }

    private static List<Row> Copy(List<Row> rows) => rows.Select(r => new Row(r, r.Comparer)).ToList();

    private static string KeyOf(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings)
    {
        var sb = new StringBuilder();
        void Add(object? part) => sb.Append(Convert.ToString(part, CultureInfo.InvariantCulture)).Append('\u0001');

        Add(ds.Provider); Add(ds.ConnectionString); Add(ds.Query); Add(ds.Source); Add(ds.Selector);
        Add(ds.Delimiter); Add(ds.HasHeaderRow); Add(ds.Headers);

        // What a run is allowed to read is part of the key, so a run that may not read a source can never be handed what another run read.
        var access = settings.Access;
        Add(access.AllowFileSources); Add(access.AllowWebSources); Add(access.AllowPrivateAddresses);
        Add(settings.FileRoot); Add(settings.AllowNetworkPaths);

        if (ds.Provider.IsFile())
        {
            var path = FileSourceLoader.ResolvePath(ds.Source, settings);
            var info = new FileInfo(path);
            Add(path); Add(info.Length); Add(info.LastWriteTimeUtc.Ticks);
        }

        foreach (var name in parameters.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            Add(name.ToUpperInvariant());
            Add(ValueText(parameters[name]));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string ValueText(object? value) => value switch
    {
        null => "\u0000",
        string s => "s:" + s,
        DateTime d => "d:" + d.ToString("o", CultureInfo.InvariantCulture),
        IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(ValueText)) + "]",
        IFormattable f => value.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().Name + ":" + value
    };
}
