using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>An image or GIF on a board, either on the canvas or attached to a note.</summary>
/// <param name="Id">Image identifier.</param>
/// <param name="BoardId">Board the image belongs to.</param>
/// <param name="NoteId">Note the image is attached to (on the note's own canvas, shown when the note is expanded), or null when it sits on the board.</param>
/// <param name="BucketId">Bucket the image sits in on the board, which carries it along when it moves; null when it is loose or attached to a note.</param>
/// <param name="FileName">Original file name, e.g. "ingest-errors.png".</param>
/// <param name="ContentType">image/png, image/jpeg, image/gif or image/webp, detected from the file itself.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="PixelWidth">Intrinsic width in pixels.</param>
/// <param name="PixelHeight">Intrinsic height in pixels.</param>
/// <param name="X">Left edge, a multiple of the 20 px grid: on the board, or on the note's canvas when attached.</param>
/// <param name="Y">Top edge, a multiple of the 20 px grid: on the board, or on the note's canvas when attached.</param>
/// <param name="Width">Display width in canvas pixels, a multiple of 20.</param>
/// <param name="Height">Display height, which follows from the width and the aspect ratio.</param>
/// <param name="Caption">Optional caption shown under the image.</param>
/// <param name="AddedBy">Current display name of whoever added the image, or "Unknown" once they no longer exist.</param>
/// <param name="AddedById">Principal id of whoever added the image, or null once they no longer exist.</param>
/// <param name="ContentUrl">Where to download the image (getImageContent). Needs the same authentication as the API.</param>
/// <param name="CreatedAt">When the image was added (UTC).</param>
/// <param name="UpdatedAt">When it was last moved, resized or re-captioned (UTC).</param>
public record ImageDetail(
    Guid Id,
    Guid BoardId,
    Guid? NoteId,
    Guid? BucketId,
    string FileName,
    string ContentType,
    long Size,
    int PixelWidth,
    int PixelHeight,
    int X,
    int Y,
    int Width,
    int Height,
    string? Caption,
    string AddedBy,
    Guid? AddedById,
    string ContentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ImageDetail From(BoardImage image) =>
        new(image.Id, image.BoardId, image.NoteId, image.BucketId, image.File!.FileName, image.File.ContentType, image.File.Size, image.PixelWidth,
            image.PixelHeight, image.X, image.Y, image.Width, image.Height, image.Caption, image.AddedBy?.ShownName ?? "Unknown", image.AddedById,
            $"/api/images/{image.Id}/content", image.CreatedAt, image.UpdatedAt);
}

/// <summary>
/// Moves, resizes and/or re-captions an image, or moves it between the board and a note. Omitted (null) fields stay
/// unchanged. X and Y are on wherever the image ends up: the board, or the note's canvas. An image that changes place
/// without X and Y goes in a free spot there.
/// </summary>
/// <param name="X">New left edge; snapped to the 20 px grid. Give it together with Y when attaching or detaching.</param>
/// <param name="Y">New top edge; snapped to the 20 px grid. Give it together with X when attaching or detaching.</param>
/// <param name="Width">New display width; snapped to the grid, between 80 and 4000. The height follows the aspect ratio.</param>
/// <param name="Caption">New caption; an empty string clears it.</param>
/// <param name="NoteId">Attach the image to this note on the same board (from the board, or from another note).</param>
/// <param name="Detach">True to take an attached image off its note and put it on the board. Do not combine with noteId.</param>
public record UpdateImageRequest(
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int? Width = null,
    [property: StringLength(500)] string? Caption = null,
    Guid? NoteId = null,
    bool? Detach = null);
