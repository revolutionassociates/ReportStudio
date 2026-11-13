using System.Globalization;
using System.Security;
using System.Text;

namespace ReportStudio.Export.Pdf;

/// <summary>
/// The XMP metadata packet of a PDF/A file. It repeats what the Info dictionary says (title, producer, dates), which
/// PDF/A insists on, and declares the conformance: part 2, level B.
/// </summary>
internal static class PdfXmp
{
    /// <summary>The text with the characters XML cannot hold taken out; a file without a title is called "Report".</summary>
    public static string CleanText(string? text)
    {
        var clean = new StringBuilder();
        foreach (var ch in text ?? "")
            if (ch == '\t' || ch == '\n' || ch == '\r' || (ch >= ' ' && !char.IsSurrogate(ch) && ch != '￾' && ch != '￿'))
                clean.Append(ch == '\n' || ch == '\r' || ch == '\t' ? ' ' : ch);
        var result = clean.ToString().Trim();
        return result.Length == 0 ? "Report" : result;
    }

    /// <summary>A moment as the Info dictionary writes it: D:20261113072400Z.</summary>
    public static string InfoDate(DateTime utc) => "D:" + utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";

    private static string XmpDate(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static byte[] Build(string title, string producer, DateTime utc)
    {
        string date = XmpDate(utc);
        var xml = new StringBuilder();
        xml.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        xml.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        xml.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        xml.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        xml.Append("<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>B</pdfaid:conformance>\n</rdf:Description>\n");
        xml.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        xml.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(SecurityElement.Escape(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        xml.Append("</rdf:Description>\n");
        xml.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        xml.Append("<pdf:Producer>").Append(SecurityElement.Escape(producer)).Append("</pdf:Producer>\n</rdf:Description>\n");
        xml.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        xml.Append("<xmp:CreatorTool>").Append(SecurityElement.Escape(producer)).Append("</xmp:CreatorTool>\n");
        xml.Append("<xmp:CreateDate>").Append(date).Append("</xmp:CreateDate>\n");
        xml.Append("<xmp:ModifyDate>").Append(date).Append("</xmp:ModifyDate>\n");
        xml.Append("<xmp:MetadataDate>").Append(date).Append("</xmp:MetadataDate>\n");
        xml.Append("</rdf:Description>\n");
        xml.Append("</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return new UTF8Encoding(false).GetBytes(xml.ToString());
    }
}
