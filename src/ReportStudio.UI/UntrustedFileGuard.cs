using ReportStudio.Core.Model;

namespace ReportStudio.UI;

/// <summary>
/// Asks the user before a report file that carries its own database query is run. Files using the Sample or
/// Host data source never prompt, because they cannot open a connection on the file's say-so.
/// </summary>
public static class UntrustedFileGuard
{
    private const int MaxQueryChars = 600;

    /// <summary>Returns true when the report may run (no own query, or the user agreed).</summary>
    public static bool Confirm(IWin32Window? owner, ReportDefinition definition, string? path)
    {
        if (!definition.HasOwnQuery) return true;

        var ds = definition.DataSource;
        var query = ds.ReadsExternalData ? ds.DescribeForTrust(MaxQueryChars) : "";

        // Drop-down lists that are read from the database are queries from the file too.
        foreach (var p in definition.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.ChoicesQuery)))
        {
            if (!ds.Provider.IsDatabase()) break;
            var choices = p.ChoicesQuery.Trim();
            if (choices.Length > MaxQueryChars) choices = choices[..MaxQueryChars] + " …";
            query += (query.Length > 0 ? "\n\n" : "") + $"List for parameter {p.Name}:\n{choices}";
        }

        // Other data sets (requirement 39) that read a database, a file or a web address are reads from the file too.
        foreach (var set in definition.DataSets.Where(d => d.DataSource.ReadsExternalData))
            query += (query.Length > 0 ? "\n\n" : "") + $"Data set {set.Name} - " + set.DataSource.DescribeForTrust(MaxQueryChars);

        var text =
            "This report file reads data from outside the report: a database query, a data file or a web address.\n" +
            "Running it will do exactly what is listed below.\n\n" +
            $"File: {path ?? "(unsaved)"}\n\n" +
            $"{query}\n\n" +
            "Only run reports from sources you trust. Run it?";

        return MessageBox.Show(owner, text, "Run report that reads outside data?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
