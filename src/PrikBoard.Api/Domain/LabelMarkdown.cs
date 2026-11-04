using System.Text;
using System.Text.RegularExpressions;

namespace PrikBoard.Api.Domain;

/// <summary>
/// A label's content as Markdown, both ways: what agents read and write. Only what labels have: <c>**bold**</c>,
/// <c>*italic*</c>, <c>~~struck through~~</c>, <c>`code`</c>, <c>[links](url)</c> and bare http(s) addresses, <c># </c> headings, nested <c>- </c>,
/// <c>1. </c> and <c>- [ ] </c> lists, <c>&gt; </c> quotes, fenced code, tables, <c>---</c> lines and paragraphs (a blank line
/// between them; a single line feed is a line break). Underline, ink and size have no Markdown form, so writing a label
/// back from Markdown loses them on its words, as a table loses its column alignment and a code block its language.
/// <c>board/labelMarkdown.ts</c> in the web app has the same rules, for pasting.
/// </summary>
public static partial class LabelMarkdown
{
    /// <summary>The blocks some Markdown holds. Check them with <see cref="BoardLabel.Check"/>: a bad link stays as text here only when it isn't http(s) or a card's path.</summary>
    public static List<LabelBlock> Parse(string markdown)
    {
        var blocks = new List<LabelBlock>();
        var paragraph = new List<string>();
        var quote = new List<string>();
        // The indents of the lists around the current item, outermost first.
        var indents = new List<int>();
        var afterItem = false;

        void EndParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new(LabelBlockKind.Paragraph, Inline(string.Join('\n', paragraph))));
            paragraph.Clear();
        }

        void EndQuote()
        {
            if (quote.Count == 0) return;
            blocks.Add(new(LabelBlockKind.Quote, Inline(string.Join('\n', quote))));
            quote.Clear();
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            var wasAfterItem = afterItem;
            afterItem = false;
            if (trimmed.Length == 0)
            {
                EndParagraph();
                EndQuote();
                continue;
            }
            if (Fence(trimmed) is { } fence)
            {
                EndParagraph();
                EndQuote();
                indents.Clear();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimEnd().TrimStart().StartsWith(fence, StringComparison.Ordinal); i++) code.Add(lines[i].TrimEnd('\r'));
                blocks.Add(new(LabelBlockKind.Code, code.Count == 0 ? [] : [new LabelRun(string.Join('\n', code))]));
                continue;
            }
            if (trimmed.TrimStart().StartsWith('>'))
            {
                EndParagraph();
                indents.Clear();
                var rest = trimmed.TrimStart()[1..];
                rest = rest.StartsWith(' ') ? rest[1..] : rest;
                if (rest.Length == 0) EndQuote();
                else quote.Add(rest);
                continue;
            }
            EndQuote();
            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                EndParagraph();
                indents.Clear();
                blocks.Add(new(LabelBlockKind.Heading, Inline(heading.Groups[2].Value), heading.Groups[1].Length));
            }
            else if (Rule().IsMatch(trimmed))
            {
                EndParagraph();
                indents.Clear();
                blocks.Add(new(LabelBlockKind.Rule, []));
            }
            else if (trimmed.Contains('|') && i + 1 < lines.Length && TableDivider().IsMatch(lines[i + 1].TrimEnd()))
            {
                EndParagraph();
                indents.Clear();
                blocks.Add(TableRow(trimmed));
                for (i += 2; i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0; i++) blocks.Add(TableRow(lines[i].TrimEnd()));
                i--;
            }
            else if (ListItem(trimmed) is { } item)
            {
                EndParagraph();
                var indent = trimmed.Length - trimmed.TrimStart().Length;
                while (indents.Count > 0 && indents[^1] > indent) indents.RemoveAt(indents.Count - 1);
                if (indents.Count == 0 || indents[^1] < indent) indents.Add(indent);
                var level = Math.Min(indents.Count - 1, BoardLabel.MaxListLevel);
                blocks.Add(new(item.Kind, Inline(item.Text), level > 0 ? level : null, item.Done ? true : null));
                afterItem = true;
            }
            else if (wasAfterItem)
            {
                // A line right after a list item goes on with it.
                var last = blocks[^1];
                blocks[^1] = last with { Runs = [.. last.Runs, .. Inline("\n" + trimmed.TrimStart())] };
                afterItem = true;
            }
            else
            {
                indents.Clear();
                paragraph.Add(trimmed);
            }
        }
        EndParagraph();
        EndQuote();
        return blocks;
    }

    /// <summary>A label's content as Markdown.</summary>
    public static string From(IEnumerable<LabelBlock> blocks)
    {
        var text = new StringBuilder();
        LabelBlock? previous = null;
        var numbers = new int[BoardLabel.MaxListLevel + 1];
        var headerDone = false;
        foreach (var block in blocks)
        {
            if (previous is { } was) text.Append(Separator(was, block));
            if (!IsListItem(block.Kind)) Array.Clear(numbers);
            if (block.Kind != LabelBlockKind.TableRow) headerDone = false;
            switch (block.Kind)
            {
                case LabelBlockKind.Heading:
                    text.Append('#', block.Level ?? 1).Append(' ').Append(BlockText(block.Runs));
                    break;
                case LabelBlockKind.Bullet or LabelBlockKind.Numbered or LabelBlockKind.Task:
                    var level = block.Level ?? 0;
                    Array.Clear(numbers, level + 1, numbers.Length - level - 1);
                    if (block.Kind == LabelBlockKind.Numbered) numbers[level]++;
                    else numbers[level] = 0;
                    text.Append(' ', level * ListIndent).Append(block.Kind switch
                    {
                        LabelBlockKind.Numbered => $"{numbers[level]}. ",
                        LabelBlockKind.Task => block.Done == true ? "- [x] " : "- [ ] ",
                        _ => "- ",
                    }).Append(BlockText(block.Runs));
                    break;
                case LabelBlockKind.Quote:
                    text.AppendJoin('\n', BlockText(block.Runs).Split('\n').Select(line => "> " + line));
                    break;
                case LabelBlockKind.Code:
                    var body = string.Concat(block.Runs.Select(r => r.Text));
                    var ticks = new string('`', Math.Max(3, LongestTicks(body) + 1));
                    text.Append(ticks).Append('\n').Append(body).Append('\n').Append(ticks);
                    break;
                case LabelBlockKind.TableRow:
                    var cells = (block.Cells ?? []).ConvertAll(cell => BlockText(cell).Replace("\n", " ").Replace("|", "\\|"));
                    text.Append("| ").AppendJoin(" | ", cells).Append(" |");
                    if (!headerDone) text.Append("\n|").AppendJoin("", cells.Select(_ => " --- |"));
                    headerDone = true;
                    break;
                case LabelBlockKind.Rule:
                    text.Append("---");
                    break;
                default:
                    text.AppendJoin('\n', BlockText(block.Runs).Split('\n').Select(line => ReadsAsBlock(line) ? EscapeStart(line) : line));
                    break;
            }
            previous = block;
        }
        return text.ToString();
    }

    private const int ListIndent = 4;

    private static bool IsListItem(LabelBlockKind kind) => kind is LabelBlockKind.Bullet or LabelBlockKind.Numbered or LabelBlockKind.Task;

    /// <summary>What goes between two blocks: items of one list and rows of one table follow each other, a quote goes on with an empty quote line, and anything else has a blank line between.</summary>
    private static string Separator(LabelBlock was, LabelBlock next)
    {
        if (IsListItem(was.Kind) && IsListItem(next.Kind) && (was.Kind == next.Kind || was.Level != next.Level)) return "\n";
        if (was.Kind == LabelBlockKind.TableRow && next.Kind == LabelBlockKind.TableRow) return "\n";
        return was.Kind == LabelBlockKind.Quote && next.Kind == LabelBlockKind.Quote ? "\n>\n" : "\n\n";
    }

    private static LabelBlock TableRow(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<List<LabelRun>>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] == '\\')
            {
                i++;
                continue;
            }
            if (i < text.Length && text[i] != '|') continue;
            cells.Add(Inline(text[start..Math.Min(i, text.Length)].Trim()));
            start = i + 1;
        }
        return new(LabelBlockKind.TableRow, [], Cells: cells);
    }

    /// <summary>The fence a line opens code with, or null.</summary>
    private static string? Fence(string line)
    {
        var text = line.TrimStart();
        var length = 0;
        while (length < text.Length && text[length] == '`') length++;
        if (length >= 3 && text.IndexOf('`', length) < 0) return text[..length];
        length = 0;
        while (length < text.Length && text[length] == '~') length++;
        return length >= 3 ? text[..length] : null;
    }

    private static int LongestTicks(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    private static bool ReadsAsBlock(string line) =>
        ListItem(line) is not null || Heading().IsMatch(line) || Rule().IsMatch(line) || line.TrimStart().StartsWith('>') || Fence(line) is not null;

    private static (LabelBlockKind Kind, string Text, bool Done)? ListItem(string line)
    {
        var text = line.TrimStart();
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
        {
            var rest = text[2..].TrimStart();
            if (rest.StartsWith("[ ] ", StringComparison.Ordinal)) return (LabelBlockKind.Task, rest[4..].TrimStart(), false);
            if (rest.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) return (LabelBlockKind.Task, rest[4..].TrimStart(), true);
            return (LabelBlockKind.Bullet, rest, false);
        }
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits > 0 && digits < text.Length - 1 && text[digits] == '.' && text[digits + 1] == ' ') return (LabelBlockKind.Numbered, text[(digits + 2)..].TrimStart(), false);
        return null;
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6}) +(.*?)(?: +#+)?$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^ {0,3}(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex TableDivider();

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
            else if (style.Link is null && c == 'h' && BareLink(text, i) is { } bare)
            {
                Flush();
                BoardLabel.Join(runs, style with { Text = bare, Link = bare });
                i += bare.Length;
            }
            else if (c == '`' && CodeSpan(text, i) is { } code)
            {
                Flush();
                BoardLabel.Join(runs, style with { Text = code.Text, Code = true });
                i = code.End;
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

    /// <summary>An http(s) address starting at <paramref name="start"/>, not inside a word, without the punctuation that ends a sentence around it; or null.</summary>
    private static string? BareLink(string text, int start)
    {
        if (start > 0 && char.IsLetterOrDigit(text[start - 1])) return null;
        if (string.CompareOrdinal(text, start, "http://", 0, 7) != 0 && string.CompareOrdinal(text, start, "https://", 0, 8) != 0) return null;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '`' or '|')) end++;
        while (end > start && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or '*' or '~' or '_' or '"' or '\'') end--;
        var url = text[start..end];
        while (url.EndsWith(')') && url.Count(ch => ch == ')') > url.Count(ch => ch == '(')) url = url[..^1];
        return url.Length <= BoardLabel.MaxLinkLength && CardLinks.IsValid(url) ? url : null;
    }

    /// <summary>The code span opened at <paramref name="start"/>: closed by as many backticks as open it, or null.</summary>
    private static (string Text, int End)? CodeSpan(string text, int start)
    {
        var ticks = 0;
        while (start + ticks < text.Length && text[start + ticks] == '`') ticks++;
        for (var j = start + ticks; j < text.Length; j++)
        {
            if (text[j] != '`') continue;
            var closing = 0;
            while (j + closing < text.Length && text[j + closing] == '`') closing++;
            if (closing == ticks)
            {
                var body = text[(start + ticks)..j];
                // A space on both sides is there to keep a backtick off the fence.
                if (body.Length > 2 && body[0] == ' ' && body[^1] == ' ') body = body[1..^1];
                return body.Length == 0 ? null : (body, j + closing);
            }
            j += closing - 1;
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
            var escaped = run.Code == true ? CodeText(body[lead..]) : Escape(body[lead..]);
            text.Append(run.Link is not { } link ? escaped : link == run.Text && BareLink(link, 0) == link ? link : $"[{escaped}]({link})");
        }
        CloseDownTo(0);
        return text.ToString();
    }

    private static string CodeText(string text)
    {
        var ticks = new string('`', LongestTicks(text) + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return ticks + pad + text + pad + ticks;
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '~' or '[' or ']' or '`') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>A paragraph's line that would read as another block otherwise: <c>- a</c> stays text as <c>\- a</c>, and <c>1. a</c> as <c>1\. a</c>.</summary>
    private static string EscapeStart(string line)
    {
        var at = line.Length - line.TrimStart().Length;
        var first = line[at];
        if (!char.IsAsciiDigit(first)) return line.Insert(at, "\\");
        return line.Insert(line.IndexOf('.'), "\\");
    }
}
