using System.Globalization;
using System.Text.Json;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data;

/// <summary>
/// Reads rows from a JSON array of objects, e.g. [{"EmpCode":"E001","Basic":85000,"JoinDate":"2019-07-15"}].
/// Values are typed from the report's field schema (Number to decimal, Date to DateTime, Boolean, Text);
/// a field without a schema entry is typed from the JSON value. Used for sample data and tests.
/// </summary>
public static class JsonRows
{
    public static List<Dictionary<string, object?>> Read(string json, DataSourceDefinition? schema = null)
    {
        var types = (schema?.Fields ?? new List<FieldDefinition>())
            .ToDictionary(f => f.Name, f => f.Type, StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The data file must be a JSON array of row objects.");

        var rows = new List<Dictionary<string, object?>>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every row in the data file must be a JSON object.");

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                row[property.Name] = Convert(property.Value, types.TryGetValue(property.Name, out var t) ? t : null);
            rows.Add(row);
        }
        return rows;
    }

    public static List<Dictionary<string, object?>> Load(string path, DataSourceDefinition? schema = null)
        => Read(File.ReadAllText(path), schema);

    private static object? Convert(JsonElement value, FieldType? type)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var inv = CultureInfo.InvariantCulture;

        switch (type)
        {
            case FieldType.Number:
                return value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : decimal.Parse(value.GetString()!, inv);
            case FieldType.Date:
                return DateTime.Parse(value.GetString()!, inv, DateTimeStyles.None);
            case FieldType.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : bool.Parse(value.GetString()!);
            case FieldType.Text:
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };
    }
}
