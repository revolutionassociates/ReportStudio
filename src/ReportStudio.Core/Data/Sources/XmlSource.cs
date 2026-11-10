using System.Xml;
using System.Xml.Linq;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>
/// Reads XML. A DTD is refused and nothing outside the document is ever fetched, so a crafted file cannot read other files, reach the
/// network or expand entities into gigabytes (the "billion laughs" attack). Names are matched without regard to namespace or case.
/// </summary>
public static class XmlSource
{
    public static List<Row> Read(Stream stream, string? selector, int maxRows = SourceLimits.MaxRows)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = SourceLimits.MaxBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new SourceException("The data is not valid XML (a DTD is not allowed): " + ex.Message, ex);
        }
        if (doc.Root == null) return new List<Row>();

        var rows = new List<Row>();
        foreach (var element in Select(doc.Root, selector))
        {
            if (rows.Count >= maxRows) throw new SourceException($"The data has more than {maxRows:N0} rows.");
            var row = new Row(StringComparer.OrdinalIgnoreCase);
            Fill(element, "", row, 0);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Element names: <c>orders/order</c> (a first name equal to the root's own is allowed), <c>//order</c> for every element so named; empty = the root's children.</summary>
    public static IEnumerable<XElement> Select(XElement root, string? selector)
    {
        var path = (selector ?? "").Trim();
        if (path.Length == 0) return root.Elements();
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var name = path[2..].Trim();
            return name.Length == 0 ? root.Elements() : root.Descendants().Where(e => Same(e, name));
        }

        var steps = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<XElement> current = new[] { root };
        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            if (i == 0 && Same(root, step)) continue;                              // "orders/order" starting at the root <orders>
            current = current.SelectMany(e => e.Elements().Where(c => Same(c, step))).ToList();
        }
        return current;
    }

    private static bool Same(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsSimple(XElement e) => !e.HasElements && !e.HasAttributes;

    private static void Fill(XElement element, string prefix, Row row, int depth)
    {
        if (depth > SourceLimits.MaxDepth) throw new SourceException($"The XML is nested deeper than {SourceLimits.MaxDepth} levels.");

        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            row[prefix + attribute.Name.LocalName] = ValueParser.Infer(attribute.Value);

        if (!element.HasElements)
        {
            var text = element.Value.Trim();
            // An element that is only text is the value itself; one with attributes keeps its text beside them.
            if (text.Length > 0 || prefix.Length == 0 && !element.HasAttributes)
                row[prefix.Length == 0 ? "Value" : prefix.TrimEnd('.')] = ValueParser.Infer(text);
            return;
        }

        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                if (IsSimple(items[0])) row[prefix + group.Key] = ValueParser.Infer(items[0].Value.Trim());
                else Fill(items[0], prefix + group.Key + ".", row, depth + 1);
            }
            else if (items.All(IsSimple))
            {
                row[prefix + group.Key] = ValueParser.Cut(string.Join(", ", items.Select(i => i.Value.Trim())));
            }
            else
            {
                for (int n = 0; n < items.Count; n++) Fill(items[n], $"{prefix}{group.Key}[{n + 1}].", row, depth + 1);
            }
            if (row.Count > SourceLimits.MaxColumns) throw new SourceException($"A row has more than {SourceLimits.MaxColumns} fields.");
        }
    }
}
