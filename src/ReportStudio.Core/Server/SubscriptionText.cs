using System.Text.RegularExpressions;

namespace ReportStudio.Core.Server;

/// <summary>
/// Fills the subject and body templates of a subscription (requirement 50): <c>{Report}</c>, <c>{Name}</c>, <c>{Date}</c>, <c>{Time}</c> and
/// <c>{Param.Company}</c> are replaced by their values; any other brace text is left as written. A subject is always one line, so a parameter
/// value with a line break in it can never add a header to the message.
/// </summary>
public static class SubscriptionText
{
    private static readonly Regex Token = new(@"\{([A-Za-z][A-Za-z0-9_.]*)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Render(string? template, IReadOnlyDictionary<string, string> values, int maxChars, bool singleLine)
    {
        ArgumentNullException.ThrowIfNull(values);
        var lookup = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var text = Token.Replace(template ?? "", m => lookup.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        if (singleLine)
        {
            var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();   // line breaks, tabs and other controls become spaces
            text = string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        text = text.Trim();
        return text.Length <= maxChars ? text : text[..maxChars];
    }
}
