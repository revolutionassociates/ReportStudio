using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReportStudio.Core.Server;

/// <summary>One saved version of a report: its number, when and by whom it was saved, an optional note, and the size and SHA-256 of the text.</summary>
public sealed record ReportVersion(int Number, DateTimeOffset SavedAt, string Author, string Note, int Bytes, string Sha256);

/// <summary>
/// The saved versions of reports (requirement 51). Each report id has its own folder under the history folder, holding one file per version
/// (<c>v0001.rsr</c>) and one small description beside it (<c>v0001.json</c>). A version is never changed once written; saving the same text
/// twice in a row makes no new version; old versions can be pruned, newest kept. The text of a version is checked against its recorded SHA-256
/// when it is read, so a damaged or hand-edited version is an error and never silently restored.
/// The report id follows the web viewer's rule for report names (and also refuses a trailing dot or space, which Windows drops from folder names), so an id can never point outside the history folder.
/// </summary>
public sealed class ReportHistory
{
    public const int MaxVersionBytes = 8 * 1024 * 1024;
    public const int MaxNoteChars = 500;
    public const int MaxAuthorChars = 100;
    public const int MaxVersionsPerReport = 9999;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public ReportHistory(string historyFolder, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyFolder);
        _root = Path.GetFullPath(historyFolder);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Letters, digits, space and - _ . ( ) only; no leading dot or space, no "..", at most 100 characters.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 100) return false;
        if (id[0] == '.' || id[0] == ' ' || id[^1] == ' ' || id[^1] == '.' || id.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')')) return false;
        return true;
    }

    /// <summary>
    /// Keeps <paramref name="reportText"/> as the next version of the report, unless it is the same as the newest version, which is returned instead.
    /// Throws ArgumentException for a bad id or a text that is empty or over <see cref="MaxVersionBytes"/>.
    /// </summary>
    public ReportVersion Record(string id, string reportText, string? author = null, string? note = null)
    {
        RequireId(id);
        if (string.IsNullOrWhiteSpace(reportText)) throw new ArgumentException("The report text is empty.", nameof(reportText));
        var bytes = new UTF8Encoding(false).GetBytes(reportText);
        if (bytes.Length > MaxVersionBytes) throw new ArgumentException("The report is bigger than the history keeps.", nameof(reportText));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        lock (_gate)
        {
            var existing = ListUnlocked(id);
            if (existing.Count > 0 && existing[0].Sha256 == hash) return existing[0];
            int number = existing.Count == 0 ? 1 : existing[0].Number + 1;
            if (number > MaxVersionsPerReport) throw new InvalidOperationException("This report has too many versions; prune the history first.");

            var version = new ReportVersion(number, _time.GetUtcNow(), Clean(author, MaxAuthorChars), Clean(note, MaxNoteChars), bytes.Length, hash);
            var folder = Folder(id);
            Directory.CreateDirectory(folder);
            WriteAtomic(Path.Combine(folder, FileStem(number) + ".rsr"), bytes);   // the text first: a description without text never exists
            WriteAtomic(Path.Combine(folder, FileStem(number) + ".json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(version, Json)));
            return version;
        }
    }

    /// <summary>The versions of a report, newest first. An id with no history gives an empty list; a damaged description is skipped.</summary>
    public IReadOnlyList<ReportVersion> List(string id)
    {
        RequireId(id);
        lock (_gate) return ListUnlocked(id);
    }

    /// <summary>The text of a version, or null when there is no such version. Throws InvalidDataException when the stored text no longer matches its checksum.</summary>
    public string? Read(string id, int number)
    {
        RequireId(id);
        lock (_gate)
        {
            var version = ListUnlocked(id).FirstOrDefault(v => v.Number == number);
            if (version == null) return null;
            var path = Path.Combine(Folder(id), FileStem(number) + ".rsr");
            if (!File.Exists(path)) throw new InvalidDataException($"Version {number} is listed but its file is missing.");
            var bytes = File.ReadAllBytes(path);
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != version.Sha256)
                throw new InvalidDataException($"Version {number} does not match its checksum; it has been changed or damaged.");
            return new UTF8Encoding(false).GetString(bytes);
        }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> versions (at least 1) and returns how many were deleted.</summary>
    public int Prune(string id, int keep)
    {
        RequireId(id);
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), "At least one version is kept.");
        lock (_gate)
        {
            int removed = 0;
            foreach (var old in ListUnlocked(id).Skip(keep))
            {
                var stem = Path.Combine(Folder(id), FileStem(old.Number));
                File.Delete(stem + ".json");   // the description first, so a half-deleted version never looks listed
                File.Delete(stem + ".rsr");
                removed++;
            }
            return removed;
        }
    }

    private List<ReportVersion> ListUnlocked(string id)
    {
        var folder = Folder(id);
        var list = new List<ReportVersion>();
        if (!Directory.Exists(folder)) return list;

        foreach (var file in Directory.EnumerateFiles(folder, "v*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != 5 || !int.TryParse(name.AsSpan(1), out var number) || number < 1) continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 16 * 1024) continue;
                var v = JsonSerializer.Deserialize<ReportVersion>(File.ReadAllText(file), Json);
                if (v != null && v.Number == number && v.Sha256 is { Length: 64 }) list.Add(v);
            }
            catch (JsonException)
            {
                // a damaged description: this version is not listed
            }
        }
        list.Sort((a, b) => b.Number.CompareTo(a.Number));
        return list;
    }

    private string Folder(string id) => Path.Combine(_root, id);

    private static string FileStem(int number) => "v" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    private static void RequireId(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("The report name is not allowed.", nameof(id));
    }

    private static string Clean(string? text, int max)
    {
        var chars = (text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var clean = string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= max ? clean : clean[..max];
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
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
