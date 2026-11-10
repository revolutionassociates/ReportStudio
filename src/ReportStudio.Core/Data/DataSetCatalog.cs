using System.Globalization;
using ReportStudio.Core.Expressions;

namespace ReportStudio.Core.Data;

using Row = Dictionary<string, object?>;

/// <summary>
/// The named data sets of one report run, with an index per key field so a lookup does not read every row (a lookup in each of
/// 10,000 rows of a 10,000-row set would otherwise be 100 million comparisons). Built once per run; never changed after.
/// </summary>
public sealed class DataSetCatalog
{
    private readonly Dictionary<string, List<Row>> _sets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, List<Row>>> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public DataSetCatalog(IEnumerable<KeyValuePair<string, List<Row>>> sets)
    {
        foreach (var (name, rows) in sets)
            if (!string.IsNullOrWhiteSpace(name)) _sets[name.Trim()] = rows ?? new List<Row>();
    }

    public IEnumerable<string> Names => _sets.Keys;

    public bool Contains(string name) => _sets.ContainsKey((name ?? "").Trim());

    /// <summary>All rows of a data set; empty when there is no such set (check <see cref="Contains"/> first to tell the two apart).</summary>
    public IReadOnlyList<Row> Rows(string name) =>
        _sets.TryGetValue((name ?? "").Trim(), out var rows) ? rows : new List<Row>();

    /// <summary>The rows of a data set whose field equals the value, in the set's own order. A missing value (null) matches nothing.</summary>
    public IReadOnlyList<Row> Matches(string name, string field, object? value)
    {
        var key = KeyOf(value);
        name = (name ?? "").Trim();
        field = (field ?? "").Trim();
        if (key == null || !_sets.TryGetValue(name, out var rows)) return Array.Empty<Row>();

        Dictionary<string, List<Row>> index;
        lock (_lock)
        {
            var id = name + "\u0001" + field;
            if (!_indexes.TryGetValue(id, out index!))
            {
                index = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var rowKey = row.TryGetValue(field, out var v) ? KeyOf(v) : FindKey(row, field);
                    if (rowKey == null) continue;
                    if (!index.TryGetValue(rowKey, out var list)) index[rowKey] = list = new List<Row>();
                    list.Add(row);
                }
                _indexes[id] = index;
            }
        }
        return index.TryGetValue(key, out var found) ? found : Array.Empty<Row>();
    }

    // Rows from a host may use another spelling of the field name ("EMPCODE" for "EmpCode").
    private static string? FindKey(Row row, string field)
    {
        foreach (var (name, value) in row)
            if (string.Equals(name, field, StringComparison.OrdinalIgnoreCase)) return KeyOf(value);
        return null;
    }

    /// <summary>
    /// The text two values are compared by. Numbers compare by value whatever their type (5, 5.0 and 5m are one key); text is compared
    /// without regard to case and surrounding spaces; a number and the same digits as text are one key too. Empty values have no key.
    /// </summary>
    public static string? KeyOf(object? value)
    {
        value = Expr.Normalize(value);
        switch (value)
        {
            case null: return null;
            case decimal d: return d.ToString("0.############################", CultureInfo.InvariantCulture);
            case string s:
                var t = s.Trim();
                return t.Length == 0 ? null : t.ToUpperInvariant();
            case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
            case bool b: return b ? "TRUE" : "FALSE";
            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                return string.IsNullOrEmpty(text) ? null : text.ToUpperInvariant();
        }
    }
}
