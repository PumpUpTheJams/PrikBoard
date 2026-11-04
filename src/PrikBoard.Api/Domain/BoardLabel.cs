using System.Text;
using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Rich text placed freely, with or without a background: on the board at <see cref="X"/>, <see cref="Y"/>, or on a card's own canvas
/// with a <see cref="CardId"/>. It stacks like an image, with no place of its own among the buckets. Its size is measured by
/// the web app, which is the only one that can, and estimated from the text for everyone else (<see cref="Estimate"/>).
/// </summary>
public class BoardLabel : ICanvasItem
{
    /// <summary>Most labels a board can hold, on its canvas and on its cards together.</summary>
    public const int MaxPerBoard = 5000;
    /// <summary>Most characters of text a label can hold.</summary>
    public const int MaxTextLength = 4000;
    /// <summary>Most blocks a label can hold: enough for <see cref="MaxTextLength"/> characters on lines of their own.</summary>
    public const int MaxBlocks = 2000;
    /// <summary>Most runs one block or table cell can hold.</summary>
    public const int MaxRuns = 2000;
    /// <summary>Most cells a table row can hold.</summary>
    public const int MaxCells = 30;
    /// <summary>The deepest a list item can be nested; 0 is the outer list.</summary>
    public const int MaxListLevel = 5;
    /// <summary>The smallest heading: levels run from 1 (largest) to this.</summary>
    public const int MaxHeadingLevel = 6;
    /// <summary>The narrowest a label's text can be wrapped.</summary>
    public const int MinWrapWidth = 60;
    /// <summary>What text too wide for this wraps at when nobody measured it or chose a width: what an agent writes.</summary>
    public const int DefaultWrapWidth = 340;
    /// <summary>The size of a card's text written by an agent or saved from a chat, like the text around it on a card.</summary>
    public const LabelSize CardTextSize = LabelSize.S;
    /// <summary>Longest link a run can hold.</summary>
    public const int MaxLinkLength = 2000;
    /// <summary>A label's measured size reaches at most this far from its corner.</summary>
    public const int MaxSize = Layout.CardCanvasMax;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public Guid? CardId { get; set; }
    /// <summary>
    /// The bucket the label sits in on the board (it moves along with it), or null when it is loose or on a card.
    /// Maintained by <see cref="Layout"/>, like <see cref="Card.BucketId"/>.
    /// </summary>
    public Guid? BucketId { get; set; }
    /// <summary>Top-left, snapped to the grid: on the board, or on the card's canvas when attached.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The width its text wraps at, in canvas pixels; null keeps each line as long as it is.</summary>
    public int? WrapWidth { get; set; }
    /// <summary>The height it keeps with little text, in canvas pixels, as its box was drawn; null is as tall as its text.</summary>
    public int? MinHeight { get; set; }
    /// <summary>Whether it has a background of its own, the panel a card's components have. Set when it is added: on on a card, off on the board.</summary>
    public bool Background { get; set; }
    /// <summary>The ink of text that doesn't set its own.</summary>
    public InkColor Ink { get; set; } = InkColor.Theme;
    /// <summary>The size of text that doesn't set its own.</summary>
    public LabelSize Size { get; set; } = LabelSize.S;
    public List<LabelBlock> Content { get; set; } = [];
    /// <summary>The text without its formatting, kept in step with <see cref="Content"/> (see <see cref="SetContent"/>).</summary>
    public string Text { get; set; } = "";
    /// <summary>The user or agent that added it; null once they no longer exist.</summary>
    public Guid? AddedById { get; set; }
    public Principal? AddedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Deleted with its card, it shares the card's stamp. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Sets checked content (see <see cref="Check"/>) and the plain text that follows from it.</summary>
    public void SetContent(List<LabelBlock> content)
    {
        Content = content;
        Text = PlainText(content);
    }

    /// <summary>The measured size, kept within what a label can be.</summary>
    public void SetSize(int width, int height) =>
        (Width, Height) = (Math.Clamp(width, 1, MaxSize), Math.Clamp(height, 1, MaxSize));

    /// <summary>Sets the width its text wraps at; null or 0 stops wrapping.</summary>
    public void SetWrap(int? width) => WrapWidth = width is > 0 ? Math.Clamp(width.Value, MinWrapWidth, MaxSize) : null;

    /// <summary>Sets the height it keeps with little text; null or 0 makes it as tall as its text.</summary>
    public void SetMinHeight(int? height) => MinHeight = height is > 0 ? Math.Min(height.Value, MaxSize) : null;

    /// <summary>Estimates the size of text nobody measured, wrapping it at <see cref="DefaultWrapWidth"/> when it has no width of its own and is wider than that.</summary>
    public void EstimateAndWrap()
    {
        Estimate();
        if (WrapWidth is not null || Width <= DefaultWrapWidth) return;
        WrapWidth = DefaultWrapWidth;
        Estimate();
    }

    /// <summary>Roughly how big the text is, for those who can't measure it: what an agent writes, until someone edits it in the web app.</summary>
    public void Estimate()
    {
        double width = 0, height = 0;
        var basePixels = Pixels(Size);
        var inner = WrapWidth is { } wrap ? wrap - PaddingX * 2 : (double?)null;

        void Line(double indent, double line, double tallest)
        {
            width = Math.Max(width, indent + line);
            height += tallest * LineHeight * (inner is { } room ? Math.Max(1, Math.Ceiling((indent + line) / room)) : 1);
        }

        foreach (var (b, block) in Content.Index())
        {
            if (b > 0) height += basePixels * BlockGap;
            if (block.Kind == LabelBlockKind.Rule)
            {
                height += basePixels * BlockGap;
                continue;
            }
            if (block.Kind == LabelBlockKind.TableRow)
            {
                width = Math.Max(width, (block.Cells ?? []).Sum(cell => cell.Sum(r => r.Text.Length) * basePixels * CharacterWidth + CellPadding));
                height += basePixels * LineHeight + CellPadding;
                continue;
            }
            var headed = block.Kind == LabelBlockKind.Heading ? basePixels * HeadingScale(block.Level ?? 1) : basePixels;
            var indent = block.Kind is LabelBlockKind.Bullet or LabelBlockKind.Numbered or LabelBlockKind.Task ? basePixels * ListIndent * ((block.Level ?? 0) + 1)
                : block.Kind == LabelBlockKind.Quote ? basePixels * ListIndent
                : 0;
            double line = 0, tallest = headed;
            foreach (var run in block.Runs)
            {
                var pixels = run.Size is { } size ? Pixels(size) : headed;
                tallest = Math.Max(tallest, pixels);
                var parts = run.Text.Split('\n');
                foreach (var (i, part) in parts.Index())
                {
                    if (i > 0)
                    {
                        Line(indent, line, tallest);
                        (line, tallest) = (0, pixels);
                    }
                    line += part.Length * pixels * CharacterWidth;
                }
            }
            Line(indent, line, tallest);
        }
        SetSize((inner is null ? (int)Math.Ceiling(width) + PaddingX * 2 : WrapWidth!.Value), Math.Max((int)Math.Ceiling(height) + PaddingY * 2, MinHeight ?? 0));
    }

    /// <summary>How much larger than the label's size a heading of a level is. The web app has the same table in <c>index.css</c>.</summary>
    private static double HeadingScale(int level) => level switch { 1 => 1.6, 2 => 1.35, 3 => 1.15, _ => 1 };

    /// <summary>The font size of a <see cref="LabelSize"/>, in canvas pixels. The web app has the same table.</summary>
    public static int Pixels(LabelSize size) => size switch
    {
        LabelSize.S => 14,
        LabelSize.L => 35,
        LabelSize.XL => 56,
        _ => 21,
    };

    /// <summary>
    /// Checks blocks from a request or a clip and returns them cleaned up (empty runs gone, false flags as null, neighboring
    /// runs of one style joined, blank blocks at either end gone), or the problem with the first bad one as (field, message).
    /// </summary>
    public static (List<LabelBlock>? Blocks, (string Field, string Message)? Problem) Check(IReadOnlyList<LabelBlock>? blocks, int maxTextLength = MaxTextLength)
    {
        if (blocks is null || blocks.Count == 0) return (null, ("content", "A label has text. To remove all of it, delete the label."));
        if (maxTextLength <= MaxTextLength && blocks.Count > MaxBlocks) return (null, ("content", $"A label has at most {MaxBlocks} blocks."));

        var clean = new List<LabelBlock>();
        foreach (var (b, block) in blocks.Index())
        {
            var (checkedBlock, bad) = CheckBlock(block, $"content[{b}]");
            if (bad is not null) return (null, bad);
            clean.Add(checkedBlock!);
        }

        var first = clean.FindIndex(b => !IsBlank(b));
        if (first < 0) return (null, ("content", "A label has text. To remove all of it, delete the label."));
        var last = clean.FindLastIndex(b => !IsBlank(b));
        clean = clean.GetRange(first, last - first + 1);
        if (PlainText(clean).Length > maxTextLength) return (null, ("content", $"A label has at most {maxTextLength} characters of text."));
        return (clean, null);
    }

    /// <summary>One block cleaned up, keeping only what its kind has.</summary>
    private static (LabelBlock? Block, (string Field, string Message)? Problem) CheckBlock(LabelBlock block, string field)
    {
        if (block.Kind == LabelBlockKind.Rule) return (new(LabelBlockKind.Rule, []), null);
        if (block.Kind == LabelBlockKind.TableRow)
        {
            if (block.Cells is not { Count: > 0 } cells) return (null, ($"{field}.cells", "A table row lists its cells."));
            if (cells.Count > MaxCells) return (null, ($"{field}.cells", $"A table row has at most {MaxCells} cells."));
            var row = new List<List<LabelRun>>();
            foreach (var (c, cell) in cells.Index())
            {
                var (cellRuns, cellProblem) = CheckRuns(cell, $"{field}.cells[{c}]");
                if (cellProblem is not null) return (null, cellProblem);
                row.Add(cellRuns!);
            }
            return (new(LabelBlockKind.TableRow, [], Cells: row), null);
        }

        var (runs, problem) = CheckRuns(block.Runs, $"{field}.runs");
        if (problem is not null) return (null, problem);
        switch (block.Kind)
        {
            case LabelBlockKind.Heading:
                if (block.Level is < 1 or > MaxHeadingLevel) return (null, ($"{field}.level", $"A heading's level is 1 to {MaxHeadingLevel}."));
                return (new(LabelBlockKind.Heading, runs!, block.Level ?? 1), null);
            case LabelBlockKind.Bullet or LabelBlockKind.Numbered or LabelBlockKind.Task:
                if (block.Level is < 0 or > MaxListLevel) return (null, ($"{field}.level", $"A list item's level is 0 to {MaxListLevel}."));
                return (new(block.Kind, runs!, block.Level is > 0 ? block.Level : null, block.Kind == LabelBlockKind.Task && block.Done == true ? true : null), null);
            case LabelBlockKind.Code:
                // Code is one plain text, with no styles of its own.
                return (new(LabelBlockKind.Code, runs!.Count == 0 ? [] : [new LabelRun(string.Concat(runs.Select(r => r.Text)))]), null);
            default:
                return (new(block.Kind, runs!), null);
        }
    }

    private static (List<LabelRun>? Runs, (string Field, string Message)? Problem) CheckRuns(List<LabelRun>? source, string field)
    {
        if (source is null) return (null, (field, "A block lists its runs of text."));
        if (source.Count > MaxRuns) return (null, (field, $"A block has at most {MaxRuns} runs."));
        var runs = new List<LabelRun>();
        foreach (var (r, run) in source.Index())
        {
            if (run.Text is null) return (null, ($"{field}[{r}].text", "A run has text."));
            if (run.Link is { Length: > 0 } link && (link.Length > MaxLinkLength || !CardLinks.IsValid(link.Trim())))
            {
                return (null, ($"{field}[{r}].link", "A link is an absolute http(s) URL or a card's path."));
            }
            if (run.Text.Length > 0) Join(runs, run.Normalized());
        }
        return (runs, null);
    }

    /// <summary>Adds a run, joining it to the last one when they are styled alike.</summary>
    internal static void Join(List<LabelRun> runs, LabelRun run)
    {
        if (runs.Count > 0 && runs[^1] with { Text = "" } == run with { Text = "" }) runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
        else runs.Add(run);
    }

    private static bool IsBlank(LabelBlock block) =>
        block.Kind != LabelBlockKind.Rule && block.AllRuns().All(r => string.IsNullOrWhiteSpace(r.Text));

    private static string PlainText(List<LabelBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var (i, block) in blocks.Index())
        {
            if (i > 0) text.Append('\n');
            foreach (var run in block.Runs) text.Append(run.Text);
            foreach (var (c, cell) in (block.Cells ?? []).Index())
            {
                if (c > 0) text.Append(" | ");
                foreach (var run in cell) text.Append(run.Text);
            }
        }
        return text.ToString();
    }

    // What Estimate goes by; the web app measures instead.
    private const double CharacterWidth = 0.55;
    private const double LineHeight = 1.15;
    private const double ListIndent = 1.3;
    private const double BlockGap = 0.4;
    private const int CellPadding = 12;
    // A label's border and padding, the same with a background or without (.label in the web app).
    private const int PaddingX = 15;
    private const int PaddingY = 11;
}

/// <summary>A size of text on a label: small, medium, large or extra large. Serialized as a string.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LabelSize>))]
public enum LabelSize
{
    S,
    M,
    L,
    XL,
}

/// <summary>What a block of a label is. Neighboring list items form one list, neighboring Quote blocks one quote, and neighboring TableRow blocks one table. Serialized as a string.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LabelBlockKind>))]
public enum LabelBlockKind
{
    Paragraph,
    Bullet,
    Numbered,
    /// <summary>A heading; its level is 1 to 6.</summary>
    Heading,
    /// <summary>A list item with a checkbox; done says whether it is ticked.</summary>
    Task,
    /// <summary>A quoted paragraph.</summary>
    Quote,
    /// <summary>Preformatted code: one run of plain text, with its line feeds and spaces kept.</summary>
    Code,
    /// <summary>A row of a table, in cells; the first row of a table is its header.</summary>
    TableRow,
    /// <summary>A horizontal line, with no text.</summary>
    Rule,
}

/// <summary>A paragraph, heading, list item, quote, piece of code, table row or line of a label.</summary>
/// <param name="Kind">Paragraph, Heading, Bullet, Numbered, Task, Quote, Code, TableRow or Rule. Neighboring list items form one list, neighboring Quote blocks one quote and neighboring TableRow blocks one table, whose first row is its header.</param>
/// <param name="Runs">The block's text, in pieces of one style. An empty list is a blank line. Empty for TableRow and Rule.</param>
/// <param name="Level">For Heading, 1 (largest) to 6. For Bullet, Numbered and Task, how deep it is nested, from 0 (the outer list) to 5.</param>
/// <param name="Done">For Task, whether it is ticked.</param>
/// <param name="Cells">For TableRow, its cells, each a list of runs.</param>
public record LabelBlock(
    LabelBlockKind Kind,
    List<LabelRun> Runs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Level = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Done = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<List<LabelRun>>? Cells = null)
{
    /// <summary>Every run of text in it, those in table cells too.</summary>
    public IEnumerable<LabelRun> AllRuns() => Cells is null ? Runs : Runs.Concat(Cells.SelectMany(c => c));
}

/// <summary>A piece of a label's text in one style. Styles left out (null) are off, or the label's own ink and size.</summary>
/// <param name="Text">The text; a line feed (\n) is a line break.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Underline">Underlined.</param>
/// <param name="Strike">Struck through.</param>
/// <param name="Ink">Its own ink, instead of the label's.</param>
/// <param name="Size">Its own size, instead of the label's.</param>
/// <param name="Link">Where it links to: an absolute http(s) URL, or another card's path (path in CardDetail).</param>
/// <param name="Code">Inline code.</param>
public record LabelRun(
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Bold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Italic = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Underline = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Strike = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InkColor? Ink = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LabelSize? Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Link = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Code = null)
{
    /// <summary>The same run with false flags and an empty link as null, so runs styled alike compare equal.</summary>
    public LabelRun Normalized() => this with
    {
        Bold = Bold == true ? true : null,
        Italic = Italic == true ? true : null,
        Underline = Underline == true ? true : null,
        Strike = Strike == true ? true : null,
        Link = string.IsNullOrWhiteSpace(Link) ? null : Link.Trim(),
        Code = Code == true ? true : null,
    };
}
