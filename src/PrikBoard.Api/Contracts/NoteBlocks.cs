using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// One block of a note's content, on the note's own canvas (shown when the post-it is expanded). Coordinates are note
/// pixels from the top-left of that canvas, multiples of the 20 px grid.
/// </summary>
/// <param name="Id">Block identifier, unique within the note.</param>
/// <param name="Kind">Text, Checklist or Links. Only the matching content field is set; the others are null.</param>
/// <param name="X">Left edge on the note's canvas.</param>
/// <param name="Y">Top edge on the note's canvas.</param>
/// <param name="Width">Width in note pixels.</param>
/// <param name="Height">Height in note pixels.</param>
/// <param name="Title">Optional heading, e.g. "Acceptance criteria".</param>
/// <param name="Text">The text of a Text block (plain text).</param>
/// <param name="Items">The items of a Checklist block, in order.</param>
/// <param name="Links">The links of a Links block, in order.</param>
public record NoteBlockDetail(
    Guid Id,
    NoteBlockKind Kind,
    int X,
    int Y,
    int Width,
    int Height,
    string? Title,
    string? Text,
    IReadOnlyList<ChecklistItem>? Items,
    IReadOnlyList<NoteLink>? Links)
{
    public static NoteBlockDetail From(NoteBlock block) =>
        new(block.Id, block.Kind, block.X, block.Y, block.Width, block.Height, block.Title,
            block.Kind == NoteBlockKind.Text ? block.Text ?? "" : null,
            block.Kind == NoteBlockKind.Checklist ? block.Items.Select(c => new ChecklistItem(c.Text, c.Done)).ToList() : null,
            block.Kind == NoteBlockKind.Links ? block.Links.Select(l => new NoteLink(l.Title, l.Url)).ToList() : null);
}

/// <summary>
/// A new block on a note's canvas. Give only the content that fits its kind: text for Text, items for Checklist,
/// links for Links. Without X and Y it goes in the first free spot; without a size it gets the default for its kind.
/// </summary>
/// <param name="Kind">Text, Checklist or Links.</param>
/// <param name="Title">Optional heading, e.g. "Acceptance criteria".</param>
/// <param name="Text">The text of a Text block.</param>
/// <param name="Items">The items of a Checklist block.</param>
/// <param name="Links">The links of a Links block.</param>
/// <param name="X">Left edge on the note's canvas; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge on the note's canvas; snapped to the 20 px grid. Give it together with X.</param>
/// <param name="Width">Width in note pixels; snapped to the grid, at least 160.</param>
/// <param name="Height">Height in note pixels; snapped to the grid, at least 80.</param>
/// <param name="Id">Only for putting back a block that was removed (addNoteBlock): the id it had. Leave it out otherwise; createNote ignores it.</param>
public record NewNoteBlock(
    [property: Required] NoteBlockKind? Kind,
    [property: StringLength(NoteBlock.MaxTitleLength)] string? Title = null,
    [property: StringLength(NoteBlock.MaxTextLength)] string? Text = null,
    [property: MaxLength(NoteBlock.MaxItems)] List<ChecklistItem>? Items = null,
    [property: MaxLength(NoteBlock.MaxLinks)] List<NoteLink>? Links = null,
    [property: Range(0, Layout.NoteCanvasMax)] int? X = null,
    [property: Range(0, Layout.NoteCanvasMax)] int? Y = null,
    [property: Range(1, Layout.MaxBlockSize)] int? Width = null,
    [property: Range(1, Layout.MaxBlockSize)] int? Height = null,
    Guid? Id = null);

/// <summary>
/// Partially updates a block: its content, position and/or size. Omitted (null) fields stay unchanged. A list that is
/// given replaces the block's whole list, so to tick off one checklist item, send the block's items from getNote with that
/// item changed. Only content that fits the block's kind is accepted.
/// </summary>
/// <param name="Title">New heading; an empty string clears it.</param>
/// <param name="Text">New text of a Text block; an empty string clears it.</param>
/// <param name="Items">Replacement items of a Checklist block.</param>
/// <param name="Links">Replacement links of a Links block.</param>
/// <param name="X">New left edge on the note's canvas; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge on the note's canvas; snapped to the 20 px grid.</param>
/// <param name="Width">New width; snapped to the grid, at least 160.</param>
/// <param name="Height">New height; snapped to the grid, at least 80.</param>
public record UpdateNoteBlockRequest(
    [property: StringLength(NoteBlock.MaxTitleLength)] string? Title = null,
    [property: StringLength(NoteBlock.MaxTextLength)] string? Text = null,
    [property: MaxLength(NoteBlock.MaxItems)] List<ChecklistItem>? Items = null,
    [property: MaxLength(NoteBlock.MaxLinks)] List<NoteLink>? Links = null,
    [property: Range(0, Layout.NoteCanvasMax)] int? X = null,
    [property: Range(0, Layout.NoteCanvasMax)] int? Y = null,
    [property: Range(1, Layout.MaxBlockSize)] int? Width = null,
    [property: Range(1, Layout.MaxBlockSize)] int? Height = null);
