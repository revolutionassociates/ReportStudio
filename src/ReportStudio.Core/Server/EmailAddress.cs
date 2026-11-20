namespace ReportStudio.Core.Server;

/// <summary>
/// The checks every recipient goes through before a report is mailed (requirement 50). They are stricter than the mail standard on purpose:
/// plain ASCII addresses only, no quoted names, no angle brackets, commas or spaces, so an address can never carry a second recipient or a
/// header line into the message.
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;
    private const int MaxLocalLength = 64;

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > MaxLength) return false;
        if (!string.Equals(address, address.Trim(), StringComparison.Ordinal)) return false;

        int at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@')) return false;

        var local = address[..at];
        var domain = address[(at + 1)..];
        return LocalIsValid(local) && DomainIsValid(domain);
    }

    private static bool LocalIsValid(string local)
    {
        if (local.Length == 0 || local.Length > MaxLocalLength) return false;
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in local)
            if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || "._%+-".Contains(c))) return false;
        return true;
    }

    private static bool DomainIsValid(string domain)
    {
        if (domain.Length < 3 || domain.Length > 253 || !domain.Contains('.')) return false;
        foreach (var label in domain.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-') return false;
            foreach (var c in label)
                if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c == '-')) return false;
        }
        return true;
    }

    /// <summary>The domain of a valid address in lower case, or null.</summary>
    public static string? Domain(string? address) =>
        IsValid(address) ? address![(address.IndexOf('@') + 1)..].ToLowerInvariant() : null;

    /// <summary>
    /// True when the address is at one of the allowed domains or at a sub-domain of one ("example.com" allows "a@mail.example.com").
    /// An empty list allows nothing: a server that is not told where it may send reports sends none.
    /// </summary>
    public static bool IsAllowed(string? address, IEnumerable<string>? allowedDomains)
    {
        var domain = Domain(address);
        if (domain == null || allowedDomains == null) return false;
        foreach (var allowed in allowedDomains)
        {
            var a = allowed?.Trim().TrimStart('@', '.').ToLowerInvariant();
            if (string.IsNullOrEmpty(a)) continue;
            if (domain == a || domain.EndsWith("." + a, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
