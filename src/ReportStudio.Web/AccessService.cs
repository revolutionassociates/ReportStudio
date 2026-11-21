using Microsoft.Extensions.Options;
using ReportStudio.Core.Server;

namespace ReportStudio.Web;

/// <summary>The "ReportStudio:Access" settings (requirement 51c). With no users listed the viewer is open, as before.</summary>
public sealed class AccessOptions
{
    public List<AccessUser> Users { get; set; } = new();

    /// <summary>How many wrong passwords for one name from one address are allowed in the lock-out time.</summary>
    public int MaxFailures { get; set; } = 5;

    /// <summary>The time (minutes) the failures are counted over, and the time a name is then blocked.</summary>
    public int LockoutMinutes { get; set; } = 5;

    /// <summary>The name the browser shows in its sign-in box.</summary>
    public string Realm { get; set; } = "ReportStudio";
}

/// <summary>What the sign-in check found: who it is, or that the address is blocked for now, or that no (or wrong) credentials came.</summary>
public sealed record AccessCheck(AccessUser? User, bool Blocked);

/// <summary>
/// Signs people in with HTTP Basic (browsers ask for the name and password themselves; use HTTPS, because Basic sends them in every request)
/// and keeps count of wrong passwords. A broken user entry in the settings stops the server when it starts, never at the first login.
/// The address used to count failures is the one the connection came from; behind a reverse proxy that is the proxy, so there the
/// proxy's own limits matter more.
/// </summary>
public sealed class AccessService
{
    public const string UserKey = "rs.user";

    private readonly AccessPolicy _policy;
    private readonly LoginThrottle _throttle;
    private readonly ILogger<AccessService> _log;
    private readonly string _realm;

    public AccessService(IOptions<AccessOptions> options, TimeProvider time, ILogger<AccessService> log)
    {
        var o = options.Value;
        try
        {
            _policy = new AccessPolicy(o.Users);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The settings ReportStudio:Access:Users are not usable: " + ex.Message, ex);
        }

        _throttle = new LoginThrottle(o.MaxFailures, TimeSpan.FromMinutes(Math.Max(1, o.LockoutMinutes)), time);
        LockoutSeconds = Math.Max(1, o.LockoutMinutes) * 60;
        _log = log;
        _realm = new string((o.Realm ?? "").Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray());
        if (_realm.Length == 0) _realm = "ReportStudio";
    }

    public bool Enabled => _policy.Count > 0;
    public int LockoutSeconds { get; }
    public string Challenge => $"Basic realm=\"{_realm}\", charset=\"UTF-8\"";

    public AccessCheck Check(HttpContext http)
    {
        if (!BasicCredentials.TryParse(http.Request.Headers.Authorization.ToString(), out var name, out var password))
            return new AccessCheck(null, false);

        var key = name.Trim().ToLowerInvariant() + "|" + http.Connection.RemoteIpAddress;
        if (_throttle.IsBlocked(key)) return new AccessCheck(null, true);

        var user = _policy.Authenticate(name, password);
        if (user == null)
        {
            _throttle.Failure(key);
            _log.LogWarning("A sign-in failed for user '{User}' from {Address}", name.Length <= 100 ? name : name[..100], http.Connection.RemoteIpAddress);
            return new AccessCheck(null, false);
        }

        _throttle.Success(key);
        return new AccessCheck(user, false);
    }
}
