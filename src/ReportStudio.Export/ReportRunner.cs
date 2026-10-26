using System.Globalization;
using ReportStudio.Core.Formatting;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Headless output for services (bulk payslips, e-mail attachments): no window, no message box.
/// Rows always come from the caller, so the definition's own query or connection string is never used.
/// Errors are thrown as exceptions for the caller to handle.
/// </summary>
public static class ReportRunner
{
    public static byte[] ToPdf(ReportDefinition definition, IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IDictionary<string, string?>? parameters = null, CultureInfo? culture = null, string? baseDirectory = null)
    {
        var result = Render(definition, rows, parameters, culture, baseDirectory);
        using var ms = new MemoryStream();
        PdfExporter.Export(result, ms);
        return ms.ToArray();
    }

    public static byte[] ToExcel(ReportDefinition definition, IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IDictionary<string, string?>? parameters = null, CultureInfo? culture = null, string? baseDirectory = null)
    {
        culture ??= IndianFormat.Culture;   // Excel must read the numbers back with the culture they were printed in
        var result = Render(definition, rows, parameters, culture, baseDirectory);
        using var ms = new MemoryStream();
        ExcelExporter.Export(result, ms, culture);
        return ms.ToArray();
    }

    /// <summary>Lays the report out on the supplied rows. Use this when several outputs are needed from one run.</summary>
    public static ReportResult Render(ReportDefinition definition, IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IDictionary<string, string?>? parameters = null, CultureInfo? culture = null, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);

        // Work on a copy forced to Host mode: whatever data source the file declares is ignored.
        var def = definition.Clone();
        def.DataSource.Provider = DbProvider.Host;
        def.Normalize();

        using var measurer = new GdiTextMeasurer();
        var options = new ReportOptions
        {
            Culture = culture ?? IndianFormat.Culture,
            BaseDirectory = baseDirectory,
            Measurer = measurer
        };
        return ReportEngine.Run(def, parameters, rows, options);
    }
}
