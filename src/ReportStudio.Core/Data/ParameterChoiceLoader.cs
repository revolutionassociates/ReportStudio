using System.Globalization;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data;

/// <summary>Builds the drop-down list of a parameter: its fixed list, or the rows of its query (requirement 31).</summary>
public static class ParameterChoiceLoader
{
    private const int MaxChoices = 1000;

    /// <summary>
    /// The choices for <paramref name="parameter"/>. A choices query runs on the report's own connection, may use the other
    /// parameters (@Name; <paramref name="parameters"/> holds their current values) and returns the value in its first column
    /// and, optionally, the text to show in its second. Reports on the Sample or Host data source have no database, so they
    /// use the fixed list only. Throws when the query fails; the caller shows the message.
    /// </summary>
    public static List<ParameterChoice> Load(ReportDefinition definition, ReportParameter parameter,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var ds = definition.DataSource;
        if (string.IsNullOrWhiteSpace(parameter.ChoicesQuery) || ds.Provider is DbProvider.Sample or DbProvider.Host)
            return ParameterChoices.Parse(parameter.Choices);

        var query = new DataSourceDefinition
        {
            Provider = ds.Provider, ConnectionString = ds.ConnectionString,
            Query = parameter.ChoicesQuery, CommandTimeoutSeconds = ds.CommandTimeoutSeconds
        };
        return FromRows(DataLoader.Load(query, parameters));
    }

    /// <summary>First column = value, second (when there is one) = label. Empty values are skipped, repeated ones kept once.</summary>
    public static List<ParameterChoice> FromRows(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var result = new List<ParameterChoice>();
        foreach (var row in rows)
        {
            var cells = row.Values.Take(2).ToList();
            if (cells.Count == 0) continue;
            var value = Text(cells[0]);
            if (value.Length == 0 || result.Any(c => c.Value == value)) continue;
            result.Add(new ParameterChoice(value, cells.Count > 1 && Text(cells[1]).Length > 0 ? Text(cells[1]) : value));
            if (result.Count >= MaxChoices) break;
        }
        return result;
    }

    private static string Text(object? v) => v switch
    {
        null or DBNull => "",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };
}
