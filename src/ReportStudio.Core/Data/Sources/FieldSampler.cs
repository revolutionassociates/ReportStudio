using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>Works out a report's field list (names and types) from the first rows of a file or web source.</summary>
public static class FieldSampler
{
    /// <summary>
    /// The fields in the order they first appear. A field is a Number, Date or Boolean when every value it has (empty ones are ignored)
    /// is of that kind, and Text when the values are mixed or all empty.
    /// </summary>
    public static List<FieldDefinition> Infer(IEnumerable<Row> rows)
    {
        var order = new List<string>();
        var kinds = new Dictionary<string, FieldType?>(StringComparer.OrdinalIgnoreCase);
        var mixed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
            foreach (var (name, value) in row)
            {
                if (!kinds.ContainsKey(name)) { kinds[name] = null; order.Add(name); }
                var kind = KindOf(value);
                if (kind == null || mixed.Contains(name)) continue;
                if (kinds[name] == null) kinds[name] = kind;
                else if (kinds[name] != kind) mixed.Add(name);
            }

        return order.Select(n => new FieldDefinition { Name = n, Type = mixed.Contains(n) ? FieldType.Text : kinds[n] ?? FieldType.Text }).ToList();
    }

    private static FieldType? KindOf(object? value) => value switch
    {
        null => null,
        string s when s.Length == 0 => null,
        string => FieldType.Text,
        bool => FieldType.Boolean,
        DateTime or DateTimeOffset => FieldType.Date,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => FieldType.Number,
        _ => FieldType.Text
    };
}
