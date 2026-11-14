namespace ReportStudio.Core.Text;

/// <summary>A piece of one line: text in one look, with the width the caller measured for it.</summary>
public sealed class RichPiece
{
    public RichPiece(TextRun run, string text, double width) { Run = run; Text = text; Width = width; }
    public TextRun Run { get; }
    public string Text { get; internal set; }
    public double Width { get; internal set; }
}

/// <summary>One finished line of rich text: pieces left to right, the width they take (without trailing spaces) and the height of the line.</summary>
public sealed class RichLine
{
    public List<RichPiece> Pieces { get; } = new();
    public double Width { get; internal set; }
    public double Height { get; internal set; }
}

/// <summary>
/// Breaks rich text into lines, the same way for the preview and the PDF. The caller supplies how wide a piece of text is in its look
/// (<c>measure</c>) and how high a line of a look is (<c>lineHeight</c>), so the rules here do not depend on a font library.
/// Rules: a new line at every "\n"; words are never split across lines unless a single word is wider than the box (then by character);
/// the spaces at a line's start (after a wrap) and end are dropped; a word made of several looks stays whole;
/// without word wrap every line is as long as its text.
/// </summary>
public static class RichLayout
{
    private abstract record Atom;
    private sealed record Newline : Atom;
    private sealed record Spaces(TextRun Run, string Text) : Atom;
    private sealed record Word(List<(TextRun Run, string Text)> Fragments) : Atom;

    public static List<RichLine> Wrap(IReadOnlyList<TextRun> runs, double maxWidth, bool wordWrap,
        Func<TextRun, string, double> measure, Func<TextRun, double> lineHeight, double emptyLineHeight)
    {
        var lines = new List<RichLine>();
        var line = new RichLine();
        double used = 0;

        void Finish()
        {
            while (line.Pieces.Count > 0 && line.Pieces[^1].Text.Trim(' ').Length == 0)
                line.Pieces.RemoveAt(line.Pieces.Count - 1);                         // no trailing spaces
            Complete(line, lineHeight, emptyLineHeight);
            lines.Add(line);
            line = new RichLine();
            used = 0;
        }
        void Add(TextRun run, string text, double width)
        {
            line.Pieces.Add(new RichPiece(run, text, width));
            used += width;
        }
        void AddWord(Word word, List<double> widths)
        {
            for (int i = 0; i < word.Fragments.Count; i++)
                Add(word.Fragments[i].Run, word.Fragments[i].Text, widths[i]);
        }

        foreach (var atom in Atoms(runs))
        {
            if (atom is Newline)
            {
                Finish();
            }
            else if (atom is Spaces spaces)
            {
                if (line.Pieces.Count > 0) Add(spaces.Run, spaces.Text, measure(spaces.Run, spaces.Text));   // spaces at a line's start are dropped
            }
            else if (atom is Word word)
            {
                var widths = word.Fragments.Select(f => measure(f.Run, f.Text)).ToList();
                double wordWidth = widths.Sum();

                if (!wordWrap || used + wordWidth <= maxWidth + 0.01)
                {
                    AddWord(word, widths);
                    continue;
                }
                if (line.Pieces.Count > 0) Finish();
                if (wordWidth <= maxWidth + 0.01)
                {
                    AddWord(word, widths);
                    continue;
                }
                // A single word wider than the box: break it by character.
                foreach (var (run, text) in word.Fragments)
                    foreach (var ch in text)
                    {
                        var s = ch.ToString();
                        double w = measure(run, s);
                        if (used + w > maxWidth + 0.01 && line.Pieces.Count > 0) Finish();
                        Add(run, s, w);
                    }
            }
        }
        if (line.Pieces.Count > 0) Finish();
        return lines;
    }

    /// <summary>The total height of the lines.</summary>
    public static double TotalHeight(IEnumerable<RichLine> lines) => lines.Sum(l => l.Height);

    private static void Complete(RichLine line, Func<TextRun, double> lineHeight, double emptyLineHeight)
    {
        // Neighbouring pieces in one look become one piece, so they are drawn as one string.
        for (int i = line.Pieces.Count - 1; i > 0; i--)
        {
            var a = line.Pieces[i - 1];
            var b = line.Pieces[i];
            if (a.Run == b.Run)
            {
                a.Text += b.Text;
                a.Width += b.Width;
                line.Pieces.RemoveAt(i);
            }
        }
        line.Width = line.Pieces.Sum(p => p.Width);
        line.Height = line.Pieces.Count == 0 ? emptyLineHeight : line.Pieces.Max(p => lineHeight(p.Run));
    }

    private static IEnumerable<Atom> Atoms(IReadOnlyList<TextRun> runs)
    {
        var fragments = new List<(TextRun Run, string Text)>();
        foreach (var run in runs)
        {
            var text = run.Text ?? "";
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n')
                {
                    if (fragments.Count > 0) { yield return new Word(fragments); fragments = new(); }
                    yield return new Newline();
                    i++;
                }
                else if (c == ' ' || c == '\t')
                {
                    if (fragments.Count > 0) { yield return new Word(fragments); fragments = new(); }
                    int start = i;
                    while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                    yield return new Spaces(run, new string(' ', i - start));
                }
                else
                {
                    int start = i;
                    while (i < text.Length && text[i] != '\n' && text[i] != ' ' && text[i] != '\t') i++;
                    fragments.Add((run, text.Substring(start, i - start)));       // a word goes on into the next run
                }
            }
        }
        if (fragments.Count > 0) yield return new Word(fragments);
    }
}
