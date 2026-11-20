using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReportStudio.Core.Server;

/// <summary>
/// The subscriptions of a server, kept in one JSON file (requirement 50). The file is read afresh on every call, so an administrator can
/// edit it by hand while the server runs, and written whole through a temporary file, so a crash never leaves half a file. One program at a
/// time owns the file; the calls of that program are serialised. A damaged file is an error, never an empty list, so subscriptions are not
/// lost by a typo.
/// </summary>
public sealed class SubscriptionStore
{
    public const int MaxSubscriptions = 1000;
    public const long MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxErrorChars = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class FileShape
    {
        public List<Subscription> Subscriptions { get; set; } = new();
    }

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public SubscriptionStore(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
    }

    public string FilePath => _path;

    /// <summary>All subscriptions. Throws InvalidDataException when the file is damaged.</summary>
    public IReadOnlyList<Subscription> All()
    {
        lock (_gate) return Load().Select(s => s.Clone()).ToList();
    }

    public Subscription? Get(string? id)
    {
        lock (_gate) return Load().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))?.Clone();
    }

    /// <summary>Adds a subscription: checks it, gives it an Id and works out its first run. Throws ArgumentException with the problems.</summary>
    public Subscription Add(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var copy = subscription.Clone();
        copy.Normalize();
        RequireValid(copy);

        lock (_gate)
        {
            var all = Load();
            if (all.Count >= MaxSubscriptions) throw new ArgumentException($"There can be at most {MaxSubscriptions} subscriptions.");

            copy.Id = Guid.NewGuid().ToString("N");
            copy.LastRun = null;
            copy.LastStatus = null;
            copy.LastError = null;
            copy.NextRun = copy.Enabled ? copy.Schedule.NextRun(_time.GetUtcNow()) : null;
            all.Add(copy);
            Save(all);
            return copy.Clone();
        }
    }

    /// <summary>Replaces what the subscription with this Id says, keeps its history, and works out the next run again. False when there is no such subscription.</summary>
    public bool Update(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var copy = subscription.Clone();
        copy.Normalize();
        RequireValid(copy);

        lock (_gate)
        {
            var all = Load();
            var index = all.FindIndex(s => string.Equals(s.Id, copy.Id, StringComparison.Ordinal));
            if (index < 0) return false;

            copy.LastRun = all[index].LastRun;
            copy.LastStatus = all[index].LastStatus;
            copy.LastError = all[index].LastError;
            copy.NextRun = copy.Enabled ? copy.Schedule.NextRun(_time.GetUtcNow()) : null;
            all[index] = copy;
            Save(all);
            return true;
        }
    }

    public bool Remove(string? id)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal)) == 0) return false;
            Save(all);
            return true;
        }
    }

    /// <summary>The enabled subscriptions whose next run is at or before <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<Subscription> Due(DateTimeOffset now)
    {
        lock (_gate)
            return Load().Where(s => s.Enabled && s.NextRun != null && s.NextRun <= now).OrderBy(s => s.NextRun).Select(s => s.Clone()).ToList();
    }

    /// <summary>
    /// Notes that a run happened and sets the next one. The next run is worked out from the present moment, so a server that was off for a
    /// week sends one report when it starts again, not seven. Returns false when the subscription has been removed meanwhile.
    /// </summary>
    public bool RecordRun(string id, bool ok, string? error)
    {
        lock (_gate)
        {
            var all = Load();
            var sub = all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (sub == null) return false;

            var now = _time.GetUtcNow();
            sub.LastRun = now;
            sub.LastStatus = ok ? "ok" : "failed";
            sub.LastError = ok || error == null ? null : (error.Length <= MaxErrorChars ? error : error[..MaxErrorChars]);
            sub.NextRun = sub.Enabled ? sub.Schedule.NextRun(now) : null;
            Save(all);
            return true;
        }
    }

    private static void RequireValid(Subscription s)
    {
        var problems = s.Problems();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
    }

    private List<Subscription> Load()
    {
        var info = new FileInfo(_path);
        if (!info.Exists) return new List<Subscription>();
        if (info.Length > MaxFileBytes) throw new InvalidDataException("The subscription file is bigger than this server allows.");

        FileShape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The subscription file is damaged: " + ex.Message, ex);
        }

        var list = shape?.Subscriptions ?? new List<Subscription>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        bool filledIn = false;   // an Id or first run added to a hand-written entry must be kept, or it would change on every read
        foreach (var s in list)
        {
            s.Normalize();
            if (s.Id.Length == 0) { s.Id = Guid.NewGuid().ToString("N"); filledIn = true; }
            if (!seen.Add(s.Id)) throw new InvalidDataException($"The subscription file has two subscriptions with the Id '{s.Id}'.");
            if (s.Enabled && s.NextRun == null)
            {
                s.NextRun = s.Schedule.NextRun(now);   // added by hand: first run worked out here
                filledIn |= s.NextRun != null;
            }
        }
        if (filledIn) Save(list);
        return list;
    }

    private void Save(List<Subscription> all)
    {
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileShape { Subscriptions = all }, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (IOException) { /* nothing more can be done */ }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
