namespace ReportStudio.Core.Layout;

/// <summary>One element of the output that contains the searched text.</summary>
public sealed record SearchHit(int PageIndex, RenderedItem Item);

/// <summary>
/// Finds text in a finished report (requirement 29): every text element whose printed text contains the search words,
/// in reading order (page, then top to bottom, then left to right). Line breaks and runs of spaces count as one space,
/// so a phrase is found even where the text wrapped.
/// </summary>
public static class ReportSearch
{
    public static List<SearchHit> Find(ReportResult report, string? query, bool matchCase = false)
    {
        var hits = new List<SearchHit>();
        var needle = Normalize(query);
        if (needle.Length == 0) return hits;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.InvariantCultureIgnoreCase;
        for (int p = 0; p < report.Pages.Count; p++)
        {
            var found = report.Pages[p].Items
                .Where(i => i.Kind == Model.ItemKind.Text && i.Text.Length > 0 && Normalize(i.Text).Contains(needle, comparison))
                .OrderBy(i => i.Y).ThenBy(i => i.X);
            hits.AddRange(found.Select(i => new SearchHit(p, i)));
        }
        return hits;
    }

    /// <summary>Trims the text and turns every run of white space (including line breaks) into one space.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
