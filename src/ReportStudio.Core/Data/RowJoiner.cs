using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data;

using Row = Dictionary<string, object?>;

/// <summary>Adds the fields of a data set to the main rows (requirement 39).</summary>
public static class RowJoiner
{
    /// <summary>
    /// Joins the rows of <paramref name="set"/> onto <paramref name="rows"/>. A row with several matches gives several rows (in the set's order);
    /// with no match it stays with empty data set fields (Left) or is dropped (Inner). The rows passed in are not changed.
    /// </summary>
    public static List<Row> Join(List<Row> rows, DataSetCatalog catalog, JoinDefinition join)
    {
        var result = new List<Row>(rows.Count);
        var setFields = catalog.Rows(join.DataSet).SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string right = join.EffectiveRightField;

        foreach (var row in rows)
        {
            row.TryGetValue(join.LeftField, out var key);
            var matches = catalog.Matches(join.DataSet, right, key);
            if (matches.Count == 0)
            {
                if (join.Kind == JoinKind.Inner) continue;
                result.Add(Merge(row, null, setFields, join.Prefix));
                continue;
            }
            foreach (var match in matches) result.Add(Merge(row, match, setFields, join.Prefix));
        }
        return result;
    }

    private static Row Merge(Row main, Row? match, List<string> setFields, string prefix)
    {
        var merged = new Row(main, StringComparer.OrdinalIgnoreCase);
        foreach (var field in setFields)
        {
            var name = prefix + field;
            if (prefix.Length == 0 && main.ContainsKey(name)) continue;       // the main row's own field wins
            object? value = null;
            if (match != null) match.TryGetValue(field, out value);
            merged[name] = value;
        }
        return merged;
    }
}
