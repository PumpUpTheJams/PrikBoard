using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A post-it note.</summary>
/// <param name="Id">Note identifier.</param>
/// <param name="ColumnId">Column the note currently sits in.</param>
/// <param name="Title">Short title shown on the post-it.</param>
/// <param name="Body">Optional longer details.</param>
/// <param name="Color">Post-it colour.</param>
/// <param name="Position">Zero-based position from the top of its column.</param>
/// <param name="CreatedAt">When the note was created (UTC).</param>
/// <param name="UpdatedAt">When the note was last edited or moved (UTC).</param>
public record NoteDetail(
    Guid Id,
    Guid ColumnId,
    string Title,
    string? Body,
    NoteColor Color,
    int Position,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static NoteDetail From(Note note) =>
        new(note.Id, note.ColumnId, note.Title, note.Body, note.Color, note.Position, note.CreatedAt, note.UpdatedAt);
}

/// <summary>Sticks a new note on a board.</summary>
/// <param name="Title">Short title shown on the post-it.</param>
/// <param name="Body">Optional longer details.</param>
/// <param name="Color">Post-it colour; defaults to Yellow.</param>
/// <param name="ColumnId">Column to place the note in; defaults to the board's first column.</param>
/// <param name="Position">Zero-based position from the top; omit to append at the bottom.</param>
public record CreateNoteRequest(
    [property: Required, StringLength(200)] string Title,
    [property: StringLength(4000)] string? Body = null,
    NoteColor? Color = null,
    Guid? ColumnId = null,
    [property: Range(0, int.MaxValue)] int? Position = null);

/// <summary>Partially updates a note's content. Omitted (null) fields stay unchanged. Use moveNote to change its column or position.</summary>
/// <param name="Title">New title.</param>
/// <param name="Body">New details; an empty string clears them.</param>
/// <param name="Color">New colour.</param>
public record UpdateNoteRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Title = null,
    [property: StringLength(4000)] string? Body = null,
    NoteColor? Color = null);

/// <summary>Moves a note to a column on the same board.</summary>
/// <param name="ColumnId">Destination column (may be the current column to reorder).</param>
/// <param name="Position">Zero-based position from the top; omit to append at the bottom.</param>
public record MoveNoteRequest(
    [property: Required] Guid ColumnId,
    [property: Range(0, int.MaxValue)] int? Position = null);
