using System.Globalization;
using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Export;

namespace ReportStudio.Web;

/// <summary>One way to download a report: the name in the address, the label, the file extension and the content type.</summary>
public sealed record DownloadFormat(string Id, string Label, string Extension, string ContentType);

/// <summary>
/// The downloads of the web viewer (requirement 49b). Only the formats in <see cref="Formats"/> can be asked for, so the address cannot
/// choose a file type or a content type. Each is written by the same exporter the designer and the command line use (the ones that need only
/// Core are compiled into this project); PDF is not here because its writer still decodes pictures with GDI+.
/// </summary>
public static class ReportDownloads
{
    public static readonly IReadOnlyList<DownloadFormat> Formats = new[]
    {
        new DownloadFormat("html", "HTML page", "html", "text/html; charset=utf-8"),
        new DownloadFormat("xlsx", "Excel workbook", "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        new DownloadFormat("csv", "CSV (comma separated)", "csv", "text/csv; charset=utf-8"),
        new DownloadFormat("docx", "Word document", "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        new DownloadFormat("rtf", "Rich text (RTF)", "rtf", "application/rtf"),
        new DownloadFormat("odt", "OpenDocument text", "odt", "application/vnd.oasis.opendocument.text"),
        new DownloadFormat("ods", "OpenDocument spreadsheet", "ods", "application/vnd.oasis.opendocument.spreadsheet")
    };

    public static DownloadFormat? Find(string? id) =>
        Formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the report in the format and returns the file's bytes.</summary>
    public static byte[] Write(DownloadFormat format, ReportResult result, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(culture);

        switch (format.Id)
        {
            case "html":
                return Encoding.UTF8.GetBytes(HtmlExporter.Build(result));
            case "docx":
                return WriteWord(result);   // the Word writer only takes a path
        }

        using var stream = new MemoryStream();
        switch (format.Id)
        {
            case "csv": CsvExporter.Export(result, stream); break;
            case "xlsx": ExcelExporter.Export(result, stream, culture); break;
            case "rtf": RtfExporter.Export(result, stream); break;
            case "odt": OdfExporter.ExportText(result, stream); break;
            case "ods": OdfExporter.ExportSpreadsheet(result, stream, culture); break;
            default: throw new ArgumentException($"The format '{format.Id}' is not offered.", nameof(format));
        }
        return stream.ToArray();
    }

    private static byte[] WriteWord(ReportResult result)
    {
        var path = Path.Combine(Path.GetTempPath(), "rs-web-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            WordExporter.Export(result, path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { /* the operating system clears the temp folder in the end */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
