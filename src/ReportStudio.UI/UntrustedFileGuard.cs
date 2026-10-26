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
        var query = ds.Query.Trim();
        if (query.Length > MaxQueryChars) query = query[..MaxQueryChars] + " …";

        var text =
            "This report file contains its own database query and connection.\n" +
            "Running it will connect to a database and execute the query below.\n\n" +
            $"File: {path ?? "(unsaved)"}\n" +
            $"Provider: {ds.Provider}\n" +
            $"Connection: {ds.MaskedConnectionString}\n\n" +
            $"Query:\n{query}\n\n" +
            "Only run reports from sources you trust. Run it?";

        return MessageBox.Show(owner, text, "Run report with its own query?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
