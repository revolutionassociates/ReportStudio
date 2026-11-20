using System.Security.Cryptography;
using System.Text;

namespace ReportStudio.Core.Server;

/// <summary>Passwords kept as salted PBKDF2-SHA256 hashes, written <c>pbkdf2-sha256$iterations$salt$hash</c> (salt and hash in base 64), so a settings file never holds a password.</summary>
public static class PasswordHash
{
    public const string Scheme = "pbkdf2-sha256";
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int MaxIterations = 5_000_000;
    public const int MaxPasswordChars = 256;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Create(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0 || password.Length > MaxPasswordChars) throw new ArgumentException($"A password has 1 to {MaxPasswordChars} characters.", nameof(password));
        if (iterations < MinIterations || iterations > MaxIterations) throw new ArgumentOutOfRangeException(nameof(iterations));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>True when the stored text is well formed (so a mistake in the settings is found at start-up, not at the first login).</summary>
    public static bool IsWellFormed(string? stored) => TryRead(stored, out _, out _, out _);

    public static bool Verify(string? password, string? stored)
    {
        if (password == null || password.Length > MaxPasswordChars || !TryRead(stored, out var iterations, out var salt, out var expected)) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryRead(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out iterations)
            || iterations < MinIterations || iterations > MaxIterations) return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length >= 8 && salt.Length <= 64 && hash.Length is >= 16 and <= 64;
    }
}

public enum AccessRole
{
    /// <summary>May open and download the reports the user is allowed.</summary>
    Viewer,

    /// <summary>A viewer of every report who may also use the administrator's interface.</summary>
    Admin
}

/// <summary>One person who may use the server (requirement 51c).</summary>
public sealed class AccessUser
{
    public const int MaxNameChars = 100;

    public string Name { get; set; } = "";

    /// <summary>A hash made by <see cref="PasswordHash.Create"/>.</summary>
    public string PasswordHash { get; set; } = "";

    public AccessRole Role { get; set; } = AccessRole.Viewer;

    /// <summary>The reports a viewer may open: report names, or a name ending in * for all names that start so ("sales-*"). Empty = every report. Ignored for an admin.</summary>
    public List<string> Reports { get; set; } = new();
}

/// <summary>
/// Who may do what on the server (requirement 51c): checks a name and password, and says which reports a user may open. Nothing here reads
/// the clock, a file or a request, so it is easy to test. A name that is not known costs the same time as a wrong password, so the answer
/// time does not tell which names exist.
/// </summary>
public sealed class AccessPolicy
{
    // Checked when the name is unknown, with the default cost, so an unknown name takes about as long as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHash.Create("not-a-real-password"));

    private readonly Dictionary<string, AccessUser> _users = new(StringComparer.OrdinalIgnoreCase);

    public AccessPolicy(IEnumerable<AccessUser>? users)
    {
        foreach (var u in users ?? Array.Empty<AccessUser>())
        {
            var problems = Problems(u);
            if (problems.Count > 0) throw new ArgumentException(problems[0], nameof(users));
            if (!_users.TryAdd(u.Name.Trim(), u)) throw new ArgumentException($"The user '{u.Name}' is listed twice.", nameof(users));
        }
    }

    public int Count => _users.Count;

    /// <summary>What is wrong with one user entry, one sentence each; empty when it can be used.</summary>
    public static IReadOnlyList<string> Problems(AccessUser? user)
    {
        var problems = new List<string>();
        if (user == null) return new[] { "A user entry is empty." };

        var name = (user.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > AccessUser.MaxNameChars) problems.Add($"A user needs a name of 1 to {AccessUser.MaxNameChars} characters.");
        else if (name.Any(c => char.IsControl(c) || c == ':')) problems.Add($"The user name '{name}' may not contain ':' or control characters.");

        if (!PasswordHash.IsWellFormed(user.PasswordHash)) problems.Add($"The password hash of user '{name}' is not valid; make one with PasswordHash.Create.");
        if (!Enum.IsDefined(user.Role)) problems.Add($"The role of user '{name}' is not Viewer or Admin.");

        foreach (var pattern in user.Reports ?? new List<string>())
        {
            var p = (pattern ?? "").Trim();
            var core = p.EndsWith('*') ? p[..^1] : p;
            if (p.Length == 0 || core.Contains('*') || !IsReportNameText(core))
                problems.Add($"The report '{p}' for user '{name}' is not a report name (a name may end with * to mean every name that starts so).");
        }
        return problems;
    }

    // A report-name-like text without the whole-name rules: a pattern such as "sales-" is only the start of a name.
    private static bool IsReportNameText(string text) =>
        text.Length <= 100 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')') && !text.Contains("..", StringComparison.Ordinal);

    /// <summary>The user with this name when the password is right, otherwise null.</summary>
    public AccessUser? Authenticate(string? name, string? password)
    {
        _users.TryGetValue((name ?? "").Trim(), out var user);
        bool ok = PasswordHash.Verify(password, user?.PasswordHash ?? DummyHash.Value);
        return ok && user != null ? user : null;
    }

    /// <summary>True when the user may open this report. An admin may open every report; an empty list means every report.</summary>
    public static bool CanView(AccessUser user, string? reportId)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrEmpty(reportId)) return false;
        if (user.Role == AccessRole.Admin || user.Reports == null || user.Reports.Count == 0) return true;

        foreach (var raw in user.Reports)
        {
            var p = (raw ?? "").Trim();
            if (p.Length == 0) continue;
            if (p.EndsWith('*'))
            {
                if (reportId.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(p, reportId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsAdmin(AccessUser user) => user.Role == AccessRole.Admin;
}

/// <summary>Reads the name and password of an HTTP Basic <c>Authorization</c> header.</summary>
public static class BasicCredentials
{
    public const int MaxHeaderChars = 1024;

    public static bool TryParse(string? header, out string name, out string password)
    {
        name = "";
        password = "";
        if (string.IsNullOrEmpty(header) || header.Length > MaxHeaderChars) return false;
        const string prefix = "Basic ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(header[prefix.Length..].Trim()));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }

        int colon = decoded.IndexOf(':');
        if (colon <= 0) return false;
        name = decoded[..colon];
        password = decoded[(colon + 1)..];
        return name.All(c => !char.IsControl(c));
    }
}

/// <summary>
/// Slows down guessing: after <c>maxFailures</c> wrong passwords for the same key (a user name together with the address it came from) within
/// <c>window</c>, that key is blocked until the window has passed. A right password clears the count. The table is limited in size, so it
/// cannot be used to fill the server's memory.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxKeys = 10_000;

    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (int Count, DateTimeOffset First)> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LoginThrottle(int maxFailures = 5, TimeSpan? window = null, TimeProvider? time = null)
    {
        _maxFailures = Math.Max(1, maxFailures);
        _window = window ?? TimeSpan.FromMinutes(5);
        _time = time ?? TimeProvider.System;
    }

    public bool IsBlocked(string key)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(key, out var f)) return false;
            if (_time.GetUtcNow() - f.First >= _window)
            {
                _failures.Remove(key);
                return false;
            }
            return f.Count >= _maxFailures;
        }
    }

    public void Failure(string key)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_failures.TryGetValue(key, out var f) && now - f.First < _window) _failures[key] = (f.Count + 1, f.First);
            else
            {
                if (_failures.Count >= MaxKeys) Purge(now);
                if (_failures.Count >= MaxKeys) return;   // still full of live entries: the new key is not counted, never the memory
                _failures[key] = (1, now);
            }
        }
    }

    public void Success(string key)
    {
        lock (_gate) _failures.Remove(key);
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var key in _failures.Where(p => now - p.Value.First >= _window).Select(p => p.Key).ToList()) _failures.Remove(key);
    }
}
