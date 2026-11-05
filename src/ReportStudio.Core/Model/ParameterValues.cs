using System.Collections;
using System.Globalization;

namespace ReportStudio.Core.Model;

/// <summary>
/// The value of a parameter that lets the user pick several (<see cref="ReportParameter.MultiValue"/>). Use it with
/// <c>In(Department, @Depts)</c> in an expression, or <c>WHERE Department IN (@Depts)</c> in the SQL.
/// </summary>
public sealed class ValueList : IReadOnlyList<object?>
{
    /// <summary>Several values are stored as one text with this between them, e.g. "Sales|Stores".</summary>
    public const char Separator = '|';

    private readonly List<object?> _items;

    public ValueList(IEnumerable<object?> items) { _items = items.ToList(); }

    public int Count => _items.Count;
    public object? this[int index] => _items[index];
    public IEnumerator<object?> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Shown on a report as a comma list: Sales, Stores.</summary>
    public override string ToString() =>
        string.Join(", ", _items.Select(i => i is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : i?.ToString() ?? ""));

    /// <summary>The stored text of several values: "A|B". Empty values are left out.</summary>
    public static string Join(IEnumerable<string> values) =>
        string.Join(Separator, values.Select(v => v.Trim()).Where(v => v.Length > 0));

    /// <summary>The values in a stored text, trimmed; empty ones are dropped.</summary>
    public static List<string> Split(string? stored) =>
        (stored ?? "").Split(Separator).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
}

/// <summary>One entry of a parameter's drop-down list: the value used by the report and the text the user sees.</summary>
public sealed record ParameterChoice(string Value, string Label);

public static class ParameterChoices
{
    /// <summary>
    /// Reads a fixed list written as <c>Active;Inactive</c> or with separate shown texts, <c>A=Active;I=Inactive</c>
    /// (the value comes first). Repeated values are kept once.
    /// </summary>
    public static List<ParameterChoice> Parse(string? text)
    {
        var result = new List<ParameterChoice>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = part.Trim();
            if (entry.Length == 0) continue;
            int eq = entry.IndexOf('=');
            var value = (eq > 0 ? entry.Substring(0, eq) : entry).Trim();
            var label = eq > 0 ? entry.Substring(eq + 1).Trim() : value;
            if (value.Length > 0 && !result.Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
                result.Add(new ParameterChoice(value, label.Length > 0 ? label : value));
        }
        return result;
    }

    /// <summary>The names listed in a "depends on" setting (separated by ; or ,), without the @.</summary>
    public static List<string> ParseNames(string? text) =>
        (text ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim().TrimStart('@')).Where(n => n.Length > 0).ToList();
}
