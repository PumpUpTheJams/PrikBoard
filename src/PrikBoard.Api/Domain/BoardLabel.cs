using System.Text;
using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Rich text placed freely, without a background: on the board at <see cref="X"/>, <see cref="Y"/>, or on a card's own canvas
/// with a <see cref="CardId"/>. It stacks like an image, with no place of its own among the buckets. Its size is measured by
/// the web app, which is the only one that can, and estimated from the text for everyone else (<see cref="Estimate"/>).
/// </summary>
public class BoardLabel : ICanvasItem
{
    /// <summary>Most labels a board can hold, on its canvas and on its cards together.</summary>
    public const int MaxPerBoard = 5000;
    /// <summary>Most characters of text a label can hold.</summary>
    public const int MaxTextLength = 4000;
    /// <summary>Most blocks a label can hold.</summary>
    public const int MaxBlocks = 200;
    /// <summary>Most runs one block can hold.</summary>
    public const int MaxRuns = 200;
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
    /// <summary>The ink of text that doesn't set its own.</summary>
    public InkColor Ink { get; set; } = InkColor.Chalk;
    /// <summary>The size of text that doesn't set its own.</summary>
    public LabelSize Size { get; set; } = LabelSize.M;
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

    /// <summary>Roughly how big the text is, for those who can't measure it: what an agent writes, until someone edits it in the web app.</summary>
    public void Estimate()
    {
        double width = 0, height = 0;
        var basePixels = Pixels(Size);
        foreach (var block in Content)
        {
            var indent = block.Kind == LabelBlockKind.Paragraph ? 0 : basePixels * ListIndent;
            double line = 0, tallest = basePixels;
            foreach (var run in block.Runs)
            {
                var pixels = run.Size is { } size ? Pixels(size) : basePixels;
                tallest = Math.Max(tallest, pixels);
                var parts = run.Text.Split('\n');
                foreach (var (i, part) in parts.Index())
                {
                    if (i > 0)
                    {
                        width = Math.Max(width, indent + line);
                        height += tallest * LineHeight;
                        (line, tallest) = (0, pixels);
                    }
                    line += part.Length * pixels * CharacterWidth;
                }
            }
            width = Math.Max(width, indent + line);
            height += tallest * LineHeight;
        }
        SetSize((int)Math.Ceiling(width) + PaddingX * 2, (int)Math.Ceiling(height) + PaddingY * 2);
    }

    /// <summary>The font size of a <see cref="LabelSize"/>, in canvas pixels. The web app has the same table.</summary>
    public static int Pixels(LabelSize size) => size switch
    {
        LabelSize.S => 16,
        LabelSize.L => 40,
        LabelSize.XL => 64,
        _ => 24,
    };

    /// <summary>
    /// Checks blocks from a request or a clip and returns them cleaned up (empty runs gone, false flags as null, neighboring
    /// runs of one style joined, blank blocks at either end gone), or the problem with the first bad one as (field, message).
    /// </summary>
    public static (List<LabelBlock>? Blocks, (string Field, string Message)? Problem) Check(IReadOnlyList<LabelBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0) return (null, ("content", "A label has text. To remove all of it, delete the label."));
        if (blocks.Count > MaxBlocks) return (null, ("content", $"A label has at most {MaxBlocks} blocks."));

        var clean = new List<LabelBlock>();
        foreach (var (b, block) in blocks.Index())
        {
            if (block.Runs is null) return (null, ($"content[{b}].runs", "A block lists its runs of text."));
            if (block.Runs.Count > MaxRuns) return (null, ($"content[{b}].runs", $"A block has at most {MaxRuns} runs."));
            var runs = new List<LabelRun>();
            foreach (var (r, run) in block.Runs.Index())
            {
                if (run.Text is null) return (null, ($"content[{b}].runs[{r}].text", "A run has text."));
                if (run.Link is { Length: > 0 } link && (link.Length > MaxLinkLength || !CardLinks.IsValid(link.Trim())))
                {
                    return (null, ($"content[{b}].runs[{r}].link", "A link is an absolute http(s) URL or a card's path."));
                }
                if (run.Text.Length > 0) Join(runs, run.Normalized());
            }
            clean.Add(block with { Runs = runs });
        }

        var first = clean.FindIndex(b => !IsBlank(b));
        if (first < 0) return (null, ("content", "A label has text. To remove all of it, delete the label."));
        var last = clean.FindLastIndex(b => !IsBlank(b));
        clean = clean.GetRange(first, last - first + 1);
        if (PlainText(clean).Length > MaxTextLength) return (null, ("content", $"A label has at most {MaxTextLength} characters of text."));
        return (clean, null);
    }

    /// <summary>Adds a run, joining it to the last one when they are styled alike.</summary>
    internal static void Join(List<LabelRun> runs, LabelRun run)
    {
        if (runs.Count > 0 && runs[^1] with { Text = "" } == run with { Text = "" }) runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
        else runs.Add(run);
    }

    private static bool IsBlank(LabelBlock block) => block.Runs.All(r => string.IsNullOrWhiteSpace(r.Text));

    private static string PlainText(List<LabelBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var (i, block) in blocks.Index())
        {
            if (i > 0) text.Append('\n');
            foreach (var run in block.Runs) text.Append(run.Text);
        }
        return text.ToString();
    }

    // What Estimate goes by; the web app measures instead.
    private const double CharacterWidth = 0.55;
    private const double LineHeight = 1.15;
    private const double ListIndent = 1.3;
    private const int PaddingX = 4;
    private const int PaddingY = 2;
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

/// <summary>What a block of a label is. Neighboring Bullet or Numbered blocks form one list; lists don't nest. Serialized as a string.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LabelBlockKind>))]
public enum LabelBlockKind
{
    Paragraph,
    Bullet,
    Numbered,
}

/// <summary>A paragraph or list item of a label.</summary>
/// <param name="Kind">Paragraph, Bullet or Numbered. Neighboring Bullet or Numbered blocks form one list; lists don't nest.</param>
/// <param name="Runs">The block's text, in pieces of one style. An empty list is a blank line.</param>
public record LabelBlock(LabelBlockKind Kind, List<LabelRun> Runs);

/// <summary>A piece of a label's text in one style. Styles left out (null) are off, or the label's own ink and size.</summary>
/// <param name="Text">The text; a line feed (\n) is a line break.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Underline">Underlined.</param>
/// <param name="Strike">Struck through.</param>
/// <param name="Ink">Its own ink, instead of the label's.</param>
/// <param name="Size">Its own size, instead of the label's.</param>
/// <param name="Link">Where it links to: an absolute http(s) URL, or another card's path (path in CardDetail).</param>
public record LabelRun(
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Bold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Italic = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Underline = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Strike = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InkColor? Ink = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LabelSize? Size = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Link = null)
{
    /// <summary>The same run with false flags and an empty link as null, so runs styled alike compare equal.</summary>
    public LabelRun Normalized() => this with
    {
        Bold = Bold == true ? true : null,
        Italic = Italic == true ? true : null,
        Underline = Underline == true ? true : null,
        Strike = Strike == true ? true : null,
        Link = string.IsNullOrWhiteSpace(Link) ? null : Link.Trim(),
    };
}
