using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Rotated text (requirement 34, fifth part) in the PDF, HTML and the preview picture.</summary>
public class TextRotationExportTests
{
    private static RenderedItem Item(double rotation, string back = "", string border = "", double width = 100, double height = 30) => new()
    {
        Kind = ItemKind.Text, X = 10, Y = 10, Width = width, Height = height, Text = "Hi", TextRotation = rotation,
        BackColor = back, BorderColor = border, BorderWidth = border.Length > 0 ? 1 : 0
    };

    private static ReportResult Result(RenderedItem item)
    {
        var result = new ReportResult { ReportName = "Turned" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.Add(item);
        result.Pages.Add(page);
        return result;
    }

    // ---- PDF ----

    private static string Contents(ReportResult result)
    {
        using var ms = new MemoryStream();
        PdfExporter.Export(result, ms);
        var text = Encoding.Latin1.GetString(ms.ToArray());
        var all = new StringBuilder();
        foreach (Match m in Regex.Matches(text, "stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(Encoding.Latin1.GetBytes(m.Groups[1].Value)), CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.Latin1);
                all.Append(reader.ReadToEnd()).Append('\n');
            }
            catch (InvalidDataException) { /* not a compressed stream */ }
        }
        return all.ToString();
    }

    [Fact]
    public void The_pdf_turns_the_text_about_the_middle_of_the_box()
    {
        var contents = Contents(Result(Item(90)));

        Assert.Contains("q\n0 -1 1 0 60 25 cm\n", contents);      // middle of the box: x 10 + 50, y 10 + 15
    }

    [Fact]
    public void The_pdf_leaves_upright_text_unturned()
    {
        Assert.DoesNotContain("0 -1 1 0", Contents(Result(Item(0))));
    }

    [Fact]
    public void The_pdf_draws_the_fill_before_the_turn()
    {
        var contents = Contents(Result(Item(90, back: "#FFFF00")));

        int fill = contents.IndexOf("100 30 re", StringComparison.Ordinal);
        int turn = contents.IndexOf("0 -1 1 0 60 25 cm", StringComparison.Ordinal);
        Assert.True(fill >= 0 && turn > fill, $"fill at {fill}, turn at {turn}");
    }

    [Fact]
    public void The_pdf_turns_nothing_when_there_is_no_text()
    {
        var item = Item(90);
        item.Text = "";

        Assert.DoesNotContain("0 -1 1 0", Contents(Result(item)));
    }

    // ---- HTML ----

    [Fact]
    public void Html_turns_the_text_in_its_own_swapped_box()
    {
        var html = HtmlExporter.Build(Result(Item(90)));

        Assert.Contains("left:45px;top:-25px;width:30px;height:100px;transform:rotate(-90deg)", html);
    }

    [Fact]
    public void Html_keeps_the_fill_and_border_upright()
    {
        var html = HtmlExporter.Build(Result(Item(90, back: "#FFFF00", border: "#FF0000")));

        Assert.Contains("left:10px;top:10px;width:100px;height:30px;background:#ffff00", html);
        Assert.Equal(1, Regex.Matches(html, "transform:rotate").Count);
    }

    [Fact]
    public void Html_does_not_turn_upright_text()
    {
        Assert.DoesNotContain("transform:rotate", HtmlExporter.Build(Result(Item(0))));
    }

    // ---- the picture ----

    private static Bitmap Draw(double rotation)
    {
        var item = new RenderedItem
        {
            Kind = ItemKind.Text, X = 0, Y = 0, Width = 100, Height = 20, Text = "MM", FontSize = 12,
            HAlign = HAlign.Left, VAlign = VAlign.Middle, WordWrap = false, TextRotation = rotation
        };
        var page = new RenderedPage { Number = 1, Width = 100, Height = 20 };
        page.Items.Add(item);
        var bmp = new Bitmap(100, 20);
        using var g = Graphics.FromImage(bmp);
        GdiPageRenderer.Draw(g, page, 1f);
        return bmp;
    }

    private static int Dark(Bitmap bmp, int fromX, int toX)
    {
        int count = 0;
        for (int x = fromX; x < toX; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R < 128 && c.G < 128 && c.B < 128) count++;
            }
        return count;
    }

    [Fact]
    public void Upright_text_sits_on_the_left_of_a_left_aligned_box()
    {
        using var bmp = Draw(0);

        Assert.True(Dark(bmp, 0, 50) > 0, "ink on the left");
        Assert.Equal(0, Dark(bmp, 55, 100));
    }

    [Fact]
    public void Text_turned_half_way_round_moves_to_the_right()
    {
        using var bmp = Draw(180);

        Assert.True(Dark(bmp, 50, 100) > 0, "ink on the right");
        Assert.Equal(0, Dark(bmp, 0, 45));
    }
}
