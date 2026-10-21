using System.Text.RegularExpressions;

namespace PrikBoard.Api.Domain;

/// <summary>
/// A short plain-text description of a card or board for link previews (the Discord bot's embeds). Cards have no
/// description field, so a card's is its first note (Text component, top to bottom, then left to right) without its
/// Markdown, or how much of its checklists is done when it has no notes.
/// </summary>
public static partial class CardPreview
{
    /// <summary>The longest description, in characters, ellipsis included.</summary>
    public const int MaxLength = 300;

    /// <summary>The card's description, or null when it has neither text nor checklist items.</summary>
    public static string? Describe(Card card)
    {
        var note = card.Components
            .Where(c => c.Kind == CardComponentKind.Text)
            .OrderBy(c => c.Y).ThenBy(c => c.X)
            .Select(c => PlainText(c.Text ?? ""))
            .FirstOrDefault(text => text.Length > 0);
        if (note is not null) return Cut(note);

        var items = card.Components.Where(c => c.Kind == CardComponentKind.Checklist).SelectMany(c => c.Items).ToList();
        return items.Count == 0 ? null : $"{items.Count(i => i.Done)}/{items.Count} done";
    }

    /// <summary>A board's description, cut like a card's; null without one.</summary>
    public static string? Describe(Board board) =>
        string.IsNullOrWhiteSpace(board.Description) ? null : Cut(PlainText(board.Description));

    /// <summary>At most <paramref name="max"/> characters: cut at the last word that fits, with an ellipsis.</summary>
    public static string Cut(string text, int max = MaxLength)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        var cut = text[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        // A single long word is cut where it must be.
        if (space > max / 2) cut = cut[..space];
        return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>
    /// Markdown as plain text on one line: code fences, headings, quotes, list markers, emphasis, images and HTML go,
    /// links keep their text, and all white space becomes single spaces. Good enough for a preview; not a parser.
    /// </summary>
    public static string PlainText(string markdown)
    {
        var text = Fence().Replace(markdown, " ");
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = Html().Replace(text, " ");
        text = LineMarker().Replace(text, "");
        text = Emphasis().Replace(text, "");
        return Space().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"^\s*(```|~~~).*$", RegexOptions.Multiline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Html();

    // Headings, quotes, bullets, numbered items, task boxes and rules at the start of a line.
    [GeneratedRegex(@"^[ \t]*(#{1,6}[ \t]+|>[ \t]?|[-*+][ \t]+(\[[ xX]\][ \t]+)?|\d+[.)][ \t]+|([-*_][ \t]*){3,}$)", RegexOptions.Multiline)]
    private static partial Regex LineMarker();

    // * and _ only at the edge of a word, so snake_case and 2*3 stay.
    [GeneratedRegex(@"~~|`|(?<!\w)[*_]+|[*_]+(?!\w)")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
