using System.Text;

namespace PrikBoard.Api.Domain;

/// <summary>
/// A label's content as Markdown, both ways: what agents read and write. Only what labels have: <c>**bold**</c>,
/// <c>*italic*</c>, <c>~~struck through~~</c>, <c>[links](url)</c>, <c>- </c> and <c>1. </c> lists, and paragraphs (a blank line
/// between them; a single line feed is a line break). Underline, ink and size have no Markdown form, so writing a label
/// back from Markdown loses them on its words. <c>board/labelMarkdown.ts</c> in the web app has the same rules, for pasting.
/// </summary>
public static class LabelMarkdown
{
    /// <summary>The blocks some Markdown holds. Check them with <see cref="BoardLabel.Check"/>: a bad link stays as text here only when it isn't http(s) or a card's path.</summary>
    public static List<LabelBlock> Parse(string markdown)
    {
        var blocks = new List<LabelBlock>();
        var paragraph = new List<string>();

        void EndParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new(LabelBlockKind.Paragraph, Inline(string.Join('\n', paragraph))));
            paragraph.Clear();
        }

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                EndParagraph();
            }
            else if (ListItem(trimmed) is { } item)
            {
                EndParagraph();
                blocks.Add(new(item.Kind, Inline(item.Text)));
            }
            else
            {
                paragraph.Add(trimmed);
            }
        }
        EndParagraph();
        return blocks;
    }

    /// <summary>A label's content as Markdown.</summary>
    public static string From(IEnumerable<LabelBlock> blocks)
    {
        var text = new StringBuilder();
        LabelBlockKind? previous = null;
        var number = 1;
        foreach (var block in blocks)
        {
            if (previous is { } was)
            {
                // Items of one list follow each other; anything else has a blank line between.
                text.Append(was == block.Kind && was != LabelBlockKind.Paragraph ? "\n" : "\n\n");
            }
            number = block.Kind == LabelBlockKind.Numbered && previous == LabelBlockKind.Numbered ? number + 1 : 1;
            text.Append(block.Kind switch
            {
                LabelBlockKind.Bullet => "- ",
                LabelBlockKind.Numbered => $"{number}. ",
                _ => "",
            });
            var body = BlockText(block.Runs);
            text.Append(block.Kind == LabelBlockKind.Paragraph && ListItem(body) is not null ? EscapeStart(body) : body);
            previous = block.Kind;
        }
        return text.ToString();
    }

    private static (LabelBlockKind Kind, string Text)? ListItem(string line)
    {
        var text = line.TrimStart();
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal)) return (LabelBlockKind.Bullet, text[2..].TrimStart());
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits > 0 && digits < text.Length - 1 && text[digits] == '.' && text[digits + 1] == ' ') return (LabelBlockKind.Numbered, text[(digits + 2)..].TrimStart());
        return null;
    }

    private static List<LabelRun> Inline(string text)
    {
        var runs = new List<LabelRun>();
        AddInline(text, new LabelRun(""), runs);
        return runs;
    }

    /// <summary>Adds <paramref name="text"/>'s runs, each styled like <paramref name="style"/> plus what its own marks say.</summary>
    private static void AddInline(string text, LabelRun style, List<LabelRun> runs)
    {
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0) return;
            BoardLabel.Join(runs, style with { Text = plain.ToString() });
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && char.IsPunctuation(text[i + 1]) | char.IsSymbol(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
            }
            else if (Mark(text, i, "**") is { } bold)
            {
                Flush();
                AddInline(text[(i + 2)..bold], style with { Bold = true }, runs);
                i = bold + 2;
            }
            else if (Mark(text, i, "~~") is { } strike)
            {
                Flush();
                AddInline(text[(i + 2)..strike], style with { Strike = true }, runs);
                i = strike + 2;
            }
            else if (Mark(text, i, "*") is { } italic)
            {
                Flush();
                AddInline(text[(i + 1)..italic], style with { Italic = true }, runs);
                i = italic + 1;
            }
            else if (c == '[' && Link(text, i) is { } link)
            {
                Flush();
                AddInline(link.Text, style with { Link = link.Url }, runs);
                i = link.End;
            }
            else
            {
                plain.Append(c);
                i++;
            }
        }
        Flush();
    }

    /// <summary>
    /// Where the mark opened at <paramref name="start"/> closes, or null when it doesn't open anything there: a mark opens
    /// next to text, and closes with text before it. In a row of asterisks, <c>**</c> closes at its end, so <c>***a***</c> is
    /// bold around italic, and <c>*</c> passes over <c>**</c>.
    /// </summary>
    private static int? Mark(string text, int start, string mark)
    {
        if (string.CompareOrdinal(text, start, mark, 0, mark.Length) != 0) return null;
        var first = start + mark.Length;
        if (first >= text.Length || char.IsWhiteSpace(text[first])) return null;
        for (var j = first; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
                continue;
            }
            if (mark == "*" && text[j] == '*' && j + 1 < text.Length && text[j + 1] == '*')
            {
                j++;
                continue;
            }
            if (string.CompareOrdinal(text, j, mark, 0, mark.Length) != 0 || j == first || char.IsWhiteSpace(text[j - 1])) continue;
            while (mark == "**" && j + 2 < text.Length && text[j + 2] == '*') j++;
            return j;
        }
        return null;
    }

    private static (string Text, string Url, int End)? Link(string text, int start)
    {
        var close = start + 1;
        while (close < text.Length && text[close] != ']') close += text[close] == '\\' ? 2 : 1;
        if (close + 1 >= text.Length || text[close + 1] != '(') return null;
        var end = text.IndexOf(')', close + 2);
        if (end < 0) return null;
        var url = text[(close + 2)..end].Trim();
        return close > start + 1 && CardLinks.IsValid(url) ? (text[(start + 1)..close], url, end + 1) : null;
    }

    /// <summary>
    /// A block's runs as text. A mark stays open across runs that share it, so bold around italic reads
    /// <c>**a *b* c**</c>. Marks hug their text: the spaces around them go outside, or they wouldn't read as marks.
    /// </summary>
    private static string BlockText(IEnumerable<LabelRun> runs)
    {
        var text = new StringBuilder();
        var open = new List<string>();

        void CloseDownTo(int keep)
        {
            if (open.Count == keep) return;
            var spaces = 0;
            while (spaces < text.Length && text[text.Length - 1 - spaces] == ' ') spaces++;
            text.Length -= spaces;
            for (var i = open.Count - 1; i >= keep; i--) text.Append(open[i]);
            text.Append(' ', spaces);
            open.RemoveRange(keep, open.Count - keep);
        }

        foreach (var run in runs)
        {
            List<string> marks = [];
            if (run.Strike == true) marks.Add("~~");
            if (run.Bold == true) marks.Add("**");
            if (run.Italic == true) marks.Add("*");

            var shared = 0;
            while (shared < open.Count && shared < marks.Count && open[shared] == marks[shared]) shared++;
            CloseDownTo(shared);

            var body = run.Text;
            var lead = shared < marks.Count ? body.Length - body.TrimStart(' ').Length : 0;
            text.Append(' ', lead);
            for (var i = shared; i < marks.Count; i++)
            {
                text.Append(marks[i]);
                open.Add(marks[i]);
            }
            var escaped = Escape(body[lead..]);
            text.Append(run.Link is { } link ? $"[{escaped}]({link})" : escaped);
        }
        CloseDownTo(0);
        return text.ToString();
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '~' or '[' or ']') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>A paragraph that would read as a list item otherwise: <c>- a</c> stays a paragraph as <c>\- a</c>, and <c>1. a</c> as <c>1\. a</c>.</summary>
    private static string EscapeStart(string body)
    {
        var dot = body.IndexOf('.');
        return body[0] is '-' or '*' || dot < 0 ? '\\' + body : body.Insert(dot, "\\");
    }
}
