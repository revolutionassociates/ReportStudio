using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Text;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>Rich text (requirement 36, second part) drawn in the preview picture and in the PDF.</summary>
public class RichTextDrawingTests
{
    private static RenderedItem Item(string text, double width, double height, params TextRun[] runs) => new()
    {
        Kind = ItemKind.Text, X = 10, Y = 10, Width = width, Height = height, Text = text, FontSize = 12, WordWrap = true,
        Runs = runs.Length > 0 ? runs.ToList() : null
    };

    private static ReportResult Result(RenderedItem item)
    {
        var result = new ReportResult { ReportName = "Rich" };
        var page = new RenderedPage { Number = 1, Width = 794, Height = 1123 };
        page.Items.Add(item);
        result.Pages.Add(page);
        return result;
    }

    // ---- preview picture ----

    private static Bitmap Draw(RenderedItem item)
    {
        var bitmap = new Bitmap(400, 200);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        GdiPageRenderer.DrawItem(g, item);
        return bitmap;
    }

    private static int Count(Bitmap bitmap, Func<Color, bool> match, int fromY = 0)
    {
        int n = 0;
        for (int y = fromY; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (match(bitmap.GetPixel(x, y))) n++;
        return n;
    }

    private static int Lowest(Bitmap bitmap)
    {
        for (int y = bitmap.Height - 1; y >= 0; y--)
            for (int x = 0; x < bitmap.Width; x++)
                if (Dark(bitmap.GetPixel(x, y))) return y;
        return -1;
    }

    private static bool Reddish(Color c) => c.R > 150 && c.G < 90 && c.B < 90;
    private static bool Dark(Color c) => c.R < 90 && c.G < 90 && c.B < 90;

    [Fact]
    public void The_preview_draws_a_coloured_run_in_its_colour_and_the_rest_in_the_item_colour()
    {
        using var bitmap = Draw(Item("Total 5 only", 300, 30, new TextRun("Total "), new TextRun("5", Color: "#FF0000"), new TextRun(" only")));

        Assert.True(Count(bitmap, Reddish) > 5);
        Assert.True(Count(bitmap, Dark) > 20);
    }

    [Fact]
    public void The_preview_draws_text_without_runs_in_one_colour()
    {
        using var bitmap = Draw(Item("Total 5 only", 300, 30));

        Assert.Equal(0, Count(bitmap, Reddish));
        Assert.True(Count(bitmap, Dark) > 20);
    }

    [Fact]
    public void The_preview_wraps_rich_text_to_the_next_line()
    {
        var runs = new[] { new TextRun("word word word word word word word word ", Bold: true), new TextRun("end") };
        using var narrow = Draw(Item("word word word word word word word word end", 80, 120, runs));
        using var wide = Draw(Item("word word word word word word word word end", 380, 120, runs));

        Assert.True(Lowest(narrow) > Lowest(wide) + 20);
    }

    [Fact]
    public void The_preview_keeps_rich_text_inside_its_box()
    {
        using var bitmap = Draw(Item("word word word word word word word word word word", 60, 20,
            new TextRun("word word word word word word word word word word", Bold: true)));

        Assert.Equal(0, Count(bitmap, Dark, fromY: 32));
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

    private static int Occurrences(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

    [Fact]
    public void The_pdf_writes_every_run_as_its_own_text_in_its_own_colour()
    {
        var contents = Contents(Result(Item("Total 5 only", 300, 30, new TextRun("Total "), new TextRun("5", Color: "#FF0000"), new TextRun(" only"))));

        Assert.Equal(3, Occurrences(contents, " Tj"));
        Assert.Contains("1 0 0 rg", contents);
    }

    [Fact]
    public void The_pdf_uses_a_bold_font_only_for_the_bold_run()
    {
        var contents = Contents(Result(Item("a b c", 300, 30, new TextRun("a "), new TextRun("b", Bold: true), new TextRun(" c"))));

        var fonts = Regex.Matches(contents, @"/(\S+) [\d.]+ Tf").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, fonts.Count);
        Assert.Equal(fonts[0], fonts[2]);
        Assert.NotEqual(fonts[0], fonts[1]);
    }

    [Fact]
    public void The_pdf_draws_underline_and_strike_through_as_lines()
    {
        var contents = Contents(Result(Item("ab", 300, 30, new TextRun("a", Underline: true), new TextRun("b", Strike: true))));

        Assert.Equal(2, Occurrences(contents, " l S"));
    }

    [Fact]
    public void The_pdf_draws_no_line_for_runs_without_underline_or_strike()
    {
        var contents = Contents(Result(Item("ab", 300, 30, new TextRun("a"), new TextRun("b", Bold: true))));

        Assert.Equal(0, Occurrences(contents, " l S"));
    }

    [Fact]
    public void The_pdf_moves_words_that_do_not_fit_to_further_lines()
    {
        var runs = new[] { new TextRun("alpha beta gamma delta epsilon zeta", Bold: true) };
        var narrow = Contents(Result(Item("alpha beta gamma delta epsilon zeta", 90, 120, runs)));
        var wide = Contents(Result(Item("alpha beta gamma delta epsilon zeta", 500, 120, runs)));

        Assert.True(Occurrences(narrow, " Tj") > Occurrences(wide, " Tj"));
        Assert.Equal(1, Occurrences(wide, " Tj"));
    }

    [Fact]
    public void The_pdf_still_draws_text_without_runs_in_one_piece()
    {
        var contents = Contents(Result(Item("plain text here", 300, 30)));

        Assert.Equal(1, Occurrences(contents, " Tj"));
    }
}
