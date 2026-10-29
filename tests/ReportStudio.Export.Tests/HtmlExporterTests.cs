using ReportStudio.Core.Barcodes;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>HTML output (requirement 14). The page model is built by hand, so no fonts or window are needed.</summary>
public class HtmlExporterTests
{
    private static ReportResult One(params RenderedItem[] items)
    {
        var result = new ReportResult { ReportName = "Pay" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.AddRange(items);
        result.Pages.Add(page);
        return result;
    }

    private static RenderedItem Text(string text) => new()
    {
        Kind = ItemKind.Text, X = 40, Y = 20, Width = 100, Height = 18, Text = text
    };

    [Fact]
    public void Each_page_is_a_sheet_of_the_report_page_size()
    {
        var result = One(Text("Asha"));
        var second = new RenderedPage { Number = 2, Width = 794, Height = 1123 };
        result.Pages.Add(second);

        var html = HtmlExporter.Build(result);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Equal(2, html.Split("<section class=\"page\" style=\"width:794px;height:1123px\">").Length - 1);
        Assert.Contains("@page{size:794px 1123px;margin:0}", html);
    }

    [Fact]
    public void Text_is_escaped_and_positioned_exactly()
    {
        var item = Text("A & <B>");
        item.Bold = true;
        item.HAlign = HAlign.Right;
        item.FontSize = 9;                       // 9 pt = 12 px
        item.ForeColor = "#FF0000";
        item.BackColor = "#00FF00";

        var html = HtmlExporter.Build(One(item));

        Assert.Contains("<div>A &amp; &lt;B&gt;</div>", html);
        Assert.Contains("left:40px;top:20px;width:100px;height:18px", html);
        Assert.Contains("font-size:12px", html);
        Assert.Contains("font-weight:700", html);
        Assert.Contains("text-align:right", html);
        Assert.Contains("color:#ff0000", html);
        Assert.Contains("background:#00ff00", html);
    }

    [Fact]
    public void The_report_name_is_the_escaped_title()
    {
        var result = One();
        result.ReportName = "Pay <2026>";
        Assert.Contains("<title>Pay &lt;2026&gt;</title>", HtmlExporter.Build(result));
    }

    [Fact]
    public void A_picture_is_embedded_as_a_data_uri()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var html = HtmlExporter.Build(One(new RenderedItem
        {
            Kind = ItemKind.Image, X = 0, Y = 0, Width = 50, Height = 50, Image = png
        }));

        Assert.Contains("src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\"", html);
    }

    [Fact]
    public void Barcodes_and_qr_codes_are_inline_svg()
    {
        var html = HtmlExporter.Build(One(
            new RenderedItem { Kind = ItemKind.Barcode, BarcodeType = BarcodeType.Code128, Text = "ABC123", X = 0, Y = 0, Width = 160, Height = 40 },
            new RenderedItem { Kind = ItemKind.QrCode, Text = "https://example.com", X = 0, Y = 60, Width = 80, Height = 80 }));

        Assert.Contains("<rect ", html);      // barcode bars
        Assert.Contains("<path ", html);      // QR modules in one path
        Assert.Contains("d=\"M", html);
    }

    [Fact]
    public void A_line_is_drawn_as_an_svg_line()
    {
        var html = HtmlExporter.Build(One(new RenderedItem
        {
            Kind = ItemKind.Line, X = 10, Y = 30, Width = 200, Height = 0, ForeColor = "#000000", BorderWidth = 1
        }));

        Assert.Contains("<line x1=\"0\" y1=\"0\" x2=\"200\" y2=\"0\"", html);
    }

    [Fact]
    public void The_file_has_no_scripts_or_external_requests()
    {
        var html = HtmlExporter.Build(One(Text("Asha")));

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void An_empty_report_still_gives_one_blank_sheet()
    {
        var html = HtmlExporter.Build(new ReportResult());
        Assert.Contains("<section class=\"page\"", html);
    }

    [Fact]
    public void Stream_and_byte_output_match_the_string()
    {
        var result = One(Text("Asha"));
        var bytes = HtmlExporter.ToBytes(result);

        using var ms = new MemoryStream();
        HtmlExporter.Export(result, ms);

        Assert.Equal(bytes, ms.ToArray());
        Assert.Equal(HtmlExporter.Build(result), System.Text.Encoding.UTF8.GetString(bytes));
    }
}
