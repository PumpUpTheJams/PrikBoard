using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A post-it note. Every note is 160 by 140 pixels.</summary>
/// <param name="Id">Note identifier.</param>
/// <param name="BoardId">Board the note is stuck on.</param>
/// <param name="BucketId">Bucket containing the note's centre, or null when the note is loose on the board.</param>
/// <param name="Title">Short title shown on the post-it.</param>
/// <param name="Body">Optional longer details.</param>
/// <param name="Color">Post-it colour.</param>
/// <param name="X">Left edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Y">Top edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="CreatedAt">When the note was created (UTC).</param>
/// <param name="UpdatedAt">When the note was last edited or moved (UTC). The most recently updated note is drawn on top.</param>
public record NoteDetail(
    Guid Id,
    Guid BoardId,
    Guid? BucketId,
    string Title,
    string? Body,
    NoteColor Color,
    int X,
    int Y,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static NoteDetail From(Note note) =>
        new(note.Id, note.BoardId, note.BucketId, note.Title, note.Body, note.Color, note.X, note.Y, note.CreatedAt, note.UpdatedAt);
}

/// <summary>
/// Sticks a new note on a board. Give a bucketId to drop it in a free spot inside that bucket, or X and Y to place it exactly.
/// With neither, it goes into the board's first bucket (or a free spot on the board when there are no buckets).
/// </summary>
/// <param name="Title">Short title shown on the post-it.</param>
/// <param name="Body">Optional longer details.</param>
/// <param name="Color">Post-it colour; defaults to Yellow.</param>
/// <param name="BucketId">Bucket to put the note in. Do not combine with X and Y.</param>
/// <param name="X">Left edge in canvas pixels; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge in canvas pixels; snapped to the 20 px grid. Give it together with X.</param>
public record CreateNoteRequest(
    [property: Required, StringLength(200)] string Title,
    [property: StringLength(4000)] string? Body = null,
    NoteColor? Color = null,
    Guid? BucketId = null,
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null);

/// <summary>Partially updates a note's content. Omitted (null) fields stay unchanged. Use moveNote to move it.</summary>
/// <param name="Title">New title.</param>
/// <param name="Body">New details; an empty string clears them.</param>
/// <param name="Color">New colour.</param>
public record UpdateNoteRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Title = null,
    [property: StringLength(4000)] string? Body = null,
    NoteColor? Color = null);

/// <summary>
/// Moves a note. Give either a bucketId (the note lands in a free spot inside that bucket)
/// or X and Y (the note goes exactly there and belongs to whichever bucket contains its centre, or none).
/// </summary>
/// <param name="BucketId">Destination bucket on the same board.</param>
/// <param name="X">New left edge in canvas pixels; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge in canvas pixels; snapped to the 20 px grid.</param>
public record MoveNoteRequest(
    Guid? BucketId = null,
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null);
