using System.Text;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>RTF output (requirement 44a). The page model is built by hand, so no fonts or window are needed.</summary>
public class RtfExporterTests
{
    private static ReportResult One(double width, double height, params RenderedItem[] items)
    {
        var result = new ReportResult { ReportName = "Pay" };
        var page = new RenderedPage { Number = 1, Width = width, Height = height };
        page.Items.AddRange(items);
        result.Pages.Add(page);
        return result;
    }

    private static RenderedItem Text(double x, double y, string text) => new()
    {
        Kind = ItemKind.Text, X = x, Y = y, Width = 100, Height = 18, Text = text
    };

    [Fact]
    public void The_file_is_a_balanced_rtf_document_in_plain_ascii()
    {
        var rtf = RtfExporter.Build(One(794, 1123, Text(40, 20, "Asha")));

        Assert.StartsWith("{\\rtf1\\ansi", rtf);
        Assert.EndsWith("}", rtf);
        Assert.All(rtf, ch => Assert.True(ch < 128));

        // Count the braces that are not escaped: they must pair up.
        int open = 0;
        for (int i = 0; i < rtf.Length; i++)
        {
            if (rtf[i] == '\\') { i++; continue; }
            if (rtf[i] == '{') open++;
            else if (rtf[i] == '}') open--;
            Assert.True(open >= 0);
        }
        Assert.Equal(0, open);
    }

    [Fact]
    public void Page_size_is_written_in_twips_and_landscape_is_marked()
    {
        var portrait = RtfExporter.Build(One(794, 1123, Text(40, 20, "A")));
        var landscape = RtfExporter.Build(One(1123, 794, Text(40, 20, "A")));

        Assert.Contains("\\paperw11910\\paperh16845", portrait);       // 794 x 15, 1123 x 15
        Assert.DoesNotContain("\\landscape", portrait);
        Assert.Contains("\\paperw16845\\paperh11910\\landscape", landscape);
    }

    [Fact]
    public void Special_characters_and_other_languages_are_escaped()
    {
        var rtf = RtfExporter.Build(One(794, 1123, Text(40, 20, "a\\b {c}\nनमस्ते")));

        Assert.Contains("a\\\\b \\{c\\}\\line ", rtf);
        Assert.Contains("\\u2344?", rtf);                                // the first letter of the Hindi word, U+0928
        Assert.DoesNotContain("नमस्ते", rtf);
    }

    [Fact]
    public void Characters_above_32767_are_written_as_negative_numbers()
    {
        // U+20B9 (the rupee sign) is 8377, below the limit; U+FF21 (a full-width A) is 65313 and must wrap to -223.
        Assert.Equal("\\u8377?", RtfExporter.Escape("\u20B9"));
        Assert.Equal("\\u-223?", RtfExporter.Escape("\uFF21"));
    }

    [Fact]
    public void Bold_italic_size_colour_and_alignment_are_written_per_cell()
    {
        var item = Text(40, 20, "Total");
        item.Bold = true;
        item.Italic = true;
        item.FontSize = 12;
        item.ForeColor = "#FF0000";
        item.BackColor = "#00FF00";
        item.HAlign = HAlign.Right;

        var rtf = RtfExporter.Build(One(794, 1123, item));

        Assert.Contains("\\red255\\green0\\blue0;", rtf);
        Assert.Contains("\\red0\\green255\\blue0;", rtf);
        // The row definition comes first, so the background is entered in the colour table before the text colour.
        Assert.Contains("\\clcbpat1", rtf);
        Assert.Contains("\\qr\\fs24\\cf2\\b\\i Total\\b0\\i0\\cell", rtf);
    }

    [Fact]
    public void Items_on_one_line_become_cells_of_one_row()
    {
        var rtf = RtfExporter.Build(One(794, 1123, Text(40, 20, "Name"), Text(300, 20, "Amount"), Text(40, 60, "Asha")));

        Assert.Equal(2, CountOf(rtf, "\\trowd"));
        Assert.Equal(2, CountOf(rtf, "\\row\n"));
        Assert.Contains("Name", rtf);
        Assert.Contains("Amount", rtf);
        Assert.Contains("Asha", rtf);
    }

    [Fact]
    public void A_report_without_items_is_still_a_valid_document()
    {
        var rtf = RtfExporter.Build(new ReportResult());

        Assert.StartsWith("{\\rtf1", rtf);
        Assert.DoesNotContain("\\trowd", rtf);
    }

    [Fact]
    public void The_report_name_is_the_document_title()
    {
        var rtf = RtfExporter.Build(One(794, 1123, Text(40, 20, "A")));

        Assert.Contains("{\\info{\\title Pay}}", rtf);
    }

    [Fact]
    public void Stream_and_byte_outputs_match_the_text()
    {
        var result = One(794, 1123, Text(40, 20, "Asha"));
        using var ms = new MemoryStream();

        RtfExporter.Export(result, ms);

        Assert.Equal(RtfExporter.Build(result), Encoding.ASCII.GetString(ms.ToArray()));
        Assert.Equal(ms.ToArray(), RtfExporter.ToBytes(result));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { count++; at += part.Length; }
        return count;
    }
}
