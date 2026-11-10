using System.Globalization;
using System.Text.Json;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>Reads JSON: an array of objects (or of values), or an object, found by a simple path.</summary>
public static class JsonSource
{
    public static List<Row> Read(Stream stream, string? selector, int maxRows = SourceLimits.MaxRows)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                MaxDepth = SourceLimits.MaxDepth, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            throw new SourceException("The data is not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var node = Select(doc.RootElement, selector);
            var rows = new List<Row>();
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    if (rows.Count >= maxRows)
                        throw new SourceException($"The data has more than {maxRows:N0} rows.");
                    rows.Add(ToRow(item));
                }
            }
            else if (node.ValueKind != JsonValueKind.Null && node.ValueKind != JsonValueKind.Undefined)
            {
                rows.Add(ToRow(node));
            }
            return rows;
        }
    }

    /// <summary>
    /// Follows a path of names and [index] steps: <c>data.items</c>, <c>$.orders[0].lines</c>, <c>[2]</c>. A leading $ or . is ignored.
    /// There are no filters or wildcards; a step that is not there is an error that names it.
    /// </summary>
    public static JsonElement Select(JsonElement root, string? selector)
    {
        var path = (selector ?? "").Trim();
        if (path.StartsWith('$')) path = path[1..];
        path = path.TrimStart('.');
        var node = root;
        if (path.Length == 0) return node;

        foreach (var step in Steps(path))
        {
            if (step.Index is int index)
            {
                if (node.ValueKind != JsonValueKind.Array || index < 0 || index >= node.GetArrayLength())
                    throw new SourceException($"The path '{selector}' has no item [{index}].");
                node = node[index];
            }
            else
            {
                if (node.ValueKind != JsonValueKind.Object || !TryGet(node, step.Name!, out var next))
                    throw new SourceException($"The path '{selector}' has no '{step.Name}'.");
                node = next;
            }
        }
        return node;
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private readonly record struct Step(string? Name, int? Index);

    private static IEnumerable<Step> Steps(string path)
    {
        int i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.') { i++; continue; }
            if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0 || !int.TryParse(path.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    throw new SourceException($"The path '{path}' has a [ ] that is not an item number.");
                yield return new Step(null, index);
                i = close + 1;
                continue;
            }
            int end = i;
            while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
            yield return new Step(path[i..end], null);
            i = end;
        }
    }

    private static Row ToRow(JsonElement item)
    {
        var row = new Row(StringComparer.OrdinalIgnoreCase);
        if (item.ValueKind == JsonValueKind.Object) Flatten(item, "", row, 0);
        else row["Value"] = Scalar(item);
        return row;
    }

    // Nested objects become "outer.inner" fields; a list of plain values becomes one text ("a, b"); anything else nested is kept as its JSON text.
    private static void Flatten(JsonElement obj, string prefix, Row row, int depth)
    {
        foreach (var p in obj.EnumerateObject())
        {
            var name = prefix + p.Name;
            if (p.Value.ValueKind == JsonValueKind.Object && depth < SourceLimits.MaxDepth)
                Flatten(p.Value, name + ".", row, depth + 1);
            else
                row[name] = p.Value.ValueKind == JsonValueKind.Array ? ArrayText(p.Value) : Scalar(p.Value);
            if (row.Count > SourceLimits.MaxColumns) throw new SourceException($"A row has more than {SourceLimits.MaxColumns} fields.");
        }
    }

    private static object? ArrayText(JsonElement array)
    {
        bool plain = array.EnumerateArray().All(e => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array));
        if (!plain) return ValueParser.Cut(array.GetRawText());
        return ValueParser.Cut(string.Join(", ", array.EnumerateArray().Select(e => Convert.ToString(Scalar(e), CultureInfo.InvariantCulture) ?? "")));
    }

    private static object? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => ValueParser.Infer(e.GetString()) is DateTime date ? date : ValueParser.Cut(e.GetString() ?? ""),
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : (object)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object or JsonValueKind.Array => ValueParser.Cut(e.GetRawText()),
        _ => null
    };
}
