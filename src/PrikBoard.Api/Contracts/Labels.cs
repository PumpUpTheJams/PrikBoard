using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>Rich text placed freely, with or without a background, on the board or on a card's canvas.</summary>
/// <param name="Id">Label identifier.</param>
/// <param name="BoardId">Board the label belongs to.</param>
/// <param name="CardId">Card the label is on (on the card's own canvas, shown when the card is expanded), or null when it sits on the board.</param>
/// <param name="BucketId">Bucket the label belongs to on the board (the topmost one containing its center when it was put there); it moves along with that bucket. Null when it is loose or on a card.</param>
/// <param name="X">Left edge, a multiple of the 20 px grid: on the board, or on the card's canvas.</param>
/// <param name="Y">Top edge, a multiple of the 20 px grid: on the board, or on the card's canvas.</param>
/// <param name="Width">Width of its text in canvas pixels, as last measured by the web app, or an estimate.</param>
/// <param name="Height">Height of its text in canvas pixels, as last measured by the web app, or an estimate.</param>
/// <param name="WrapWidth">The width its text wraps at, in canvas pixels; null when each line is as long as it is.</param>
/// <param name="Background">Whether it has a background of its own.</param>
/// <param name="Ink">Ink of text that doesn't set its own: Theme follows the theme's text color; the others keep their colors in every theme, so some are hard to read on some boards.</param>
/// <param name="Size">Size of text that doesn't set its own: S (14 px), M (21), L (35) or XL (56).</param>
/// <param name="Content">The text with its formatting, in paragraphs and list items.</param>
/// <param name="Text">The text without its formatting, blocks on lines of their own.</param>
/// <param name="AddedBy">Current display name of whoever added it, or "Unknown" once they no longer exist.</param>
/// <param name="AddedById">Principal id of whoever added it, or null once they no longer exist.</param>
/// <param name="CreatedAt">When it was added (UTC).</param>
/// <param name="UpdatedAt">When its text, place, ink or size last changed (UTC).</param>
public record LabelDetail(
    Guid Id,
    Guid BoardId,
    Guid? CardId,
    Guid? BucketId,
    int X,
    int Y,
    int Width,
    int Height,
    int? WrapWidth,
    bool Background,
    InkColor Ink,
    LabelSize Size,
    IReadOnlyList<LabelBlock> Content,
    string Text,
    string AddedBy,
    Guid? AddedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static LabelDetail From(BoardLabel label) =>
        new(label.Id, label.BoardId, label.CardId, label.BucketId, label.X, label.Y, label.Width, label.Height, label.WrapWidth, label.Background, label.Ink, label.Size,
            label.Content, label.Text, label.AddedBy?.ShownName ?? "Unknown", label.AddedById, label.CreatedAt, label.UpdatedAt);
}

/// <summary>
/// A new label. Give its text as content, or as markdown (which has no underline, and no ink or size of its own on words).
/// Without x and y it goes in a free spot. Without width and height the server estimates them from the text.
/// </summary>
/// <param name="Content">The text with its formatting, in blocks. Give this or markdown.</param>
/// <param name="Markdown">The text as Markdown: **bold**, *italic*, ~~struck through~~, `code`, [links](url), "# " headings, nested "- ", "1. " and "- [ ] " lists, "> " quotes, ``` code blocks, tables and "---" lines, with a blank line between paragraphs. Give this or content.</param>
/// <param name="X">Left edge: on the board (-100000 to 100000), or on the card's canvas with cardId. Snapped to the grid. Give it with y.</param>
/// <param name="Y">Top edge, like x.</param>
/// <param name="Ink">Ink of text that doesn't set its own; default Theme, the theme's own text color.</param>
/// <param name="Size">Size of text that doesn't set its own: S (14 px), M (21, the default), L (35) or XL (56).</param>
/// <param name="Width">Width of the text as measured, in canvas pixels. Give it with height.</param>
/// <param name="Height">Height of the text as measured, in canvas pixels. Give it with width.</param>
/// <param name="WrapWidth">The width its text wraps at, in canvas pixels. Without one, text nobody measured wraps at 340 when it is wider than that.</param>
/// <param name="Background">Whether it has a background of its own. Without this, on a card's canvas it has one, and on the board it doesn't.</param>
/// <param name="CardId">Put it on this card's own canvas (on the same board) instead of on the board.</param>
/// <param name="BucketId">With x and y on the board: the bucket it joins, which it must overlap (without one it joins the topmost one containing its center).</param>
/// <param name="Loose">With x and y on the board: true to join no bucket, even over one. Do not combine with bucketId.</param>
public record CreateLabelRequest(
    [property: MaxLength(BoardLabel.MaxBlocks)] List<LabelBlock>? Content = null,
    [property: StringLength(BoardLabel.MaxTextLength * 2)] string? Markdown = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    InkColor Ink = InkColor.Theme,
    LabelSize Size = LabelSize.M,
    [property: Range(1, BoardLabel.MaxSize)] int? Width = null,
    [property: Range(1, BoardLabel.MaxSize)] int? Height = null,
    Guid? CardId = null,
    Guid? BucketId = null,
    bool? Loose = null,
    [property: Range(1, BoardLabel.MaxSize)] int? WrapWidth = null,
    bool? Background = null);

/// <summary>
/// Changes a label, or moves it between the board and a card. Omitted (null) fields stay unchanged. X and Y are on wherever
/// the label ends up: the board, or the card's canvas. Give both when detaching; attaching without them finds a free spot.
/// </summary>
/// <param name="Content">New text with its formatting, replacing all of it. Do not combine with markdown.</param>
/// <param name="Markdown">New text as Markdown, replacing all of it. The label's own ink and size stay, but underline and any ink or size on single words are lost, as Markdown has no form for them. Do not combine with content.</param>
/// <param name="X">New left edge; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge; snapped to the 20 px grid.</param>
/// <param name="Ink">New ink for text that doesn't set its own.</param>
/// <param name="Size">New size for text that doesn't set its own.</param>
/// <param name="Width">Width of the text as measured, in canvas pixels. Give it with height. Without them, a change of text or size estimates new ones.</param>
/// <param name="Height">Height of the text as measured, in canvas pixels. Give it with width.</param>
/// <param name="CardId">Put the label on this card's canvas (on the same board), from the board or from another card.</param>
/// <param name="Detach">True to take a label off its card and put it on the board. Do not combine with cardId.</param>
/// <param name="BucketId">With x and y on the board: the bucket it joins, which it must overlap (without one it joins the topmost one containing its center).</param>
/// <param name="Loose">With x and y on the board: true to join no bucket, even over one. Do not combine with bucketId.</param>
/// <param name="WrapWidth">The width its text wraps at, in canvas pixels; 0 stops wrapping.</param>
/// <param name="Background">Whether it has a background of its own. It stays as it is when the label moves between the board and a card.</param>
/// <param name="AppendText">Markdown to add at the end, keeping the formatting of what is there. May come with content or markdown, after which it goes.</param>
public record UpdateLabelRequest(
    [property: MaxLength(BoardLabel.MaxBlocks)] List<LabelBlock>? Content = null,
    [property: StringLength(BoardLabel.MaxTextLength * 2)] string? Markdown = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    InkColor? Ink = null,
    LabelSize? Size = null,
    [property: Range(1, BoardLabel.MaxSize)] int? Width = null,
    [property: Range(1, BoardLabel.MaxSize)] int? Height = null,
    Guid? CardId = null,
    bool? Detach = null,
    Guid? BucketId = null,
    bool? Loose = null,
    [property: Range(0, BoardLabel.MaxSize)] int? WrapWidth = null,
    bool? Background = null,
    [property: StringLength(BoardLabel.MaxTextLength * 2)] string? AppendText = null);
