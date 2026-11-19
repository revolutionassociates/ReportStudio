using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data.Sources;

/// <summary>
/// What a program that runs report files without a person at the console (the command-line runner, the web viewer) must decide before it
/// runs one (requirements 48 and 49). Data files and web addresses are allowed or not as a kind (<see cref="SourceAccess"/>); a database
/// query written in the report file needs the program's owner to say, once, that reports from that place are trusted.
/// </summary>
public static class TrustCheck
{
    /// <summary>
    /// True when running the report would send a query from the file to a database: the main query, a drop-down list that is read from the
    /// database, or a data set that reads a database. Files and web addresses do not count here.
    /// </summary>
    public static bool NeedsDatabaseTrust(ReportDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var ds = def.DataSource;
        if (ds.Provider.IsDatabase() && (ds.ReadsExternalData || def.Parameters.Any(p => !string.IsNullOrWhiteSpace(p.ChoicesQuery))))
            return true;
        return def.DataSets.Any(d => d.DataSource.Provider.IsDatabase() && d.DataSource.ReadsExternalData);
    }

    /// <summary>What the report reads from outside, one entry per source, for a log or a refusal message (long queries are cut at <paramref name="maxChars"/>).</summary>
    public static IReadOnlyList<string> Describe(ReportDefinition def, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(def);
        var lines = new List<string>();
        var ds = def.DataSource;
        if (ds.ReadsExternalData) lines.Add(ds.DescribeForTrust(maxChars));

        if (ds.Provider.IsDatabase())
        {
            foreach (var p in def.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.ChoicesQuery)))
            {
                var q = p.ChoicesQuery.Trim();
                lines.Add($"List for parameter {p.Name}: {(q.Length > maxChars ? q[..maxChars] + " ..." : q)}");
            }
        }

        foreach (var set in def.DataSets.Where(d => d.DataSource.ReadsExternalData))
            lines.Add($"Data set {set.Name} - " + set.DataSource.DescribeForTrust(maxChars));
        return lines;
    }
}
