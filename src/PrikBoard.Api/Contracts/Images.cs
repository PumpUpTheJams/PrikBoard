using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>An image or GIF on a board, either on the canvas or attached to a note.</summary>
/// <param name="Id">Image identifier.</param>
/// <param name="BoardId">Board the image belongs to.</param>
/// <param name="NoteId">Note the image is attached to (shown when the note is expanded), or null when it sits on the canvas.</param>
/// <param name="FileName">Original file name, e.g. "ingest-errors.png".</param>
/// <param name="ContentType">image/png, image/jpeg, image/gif or image/webp, detected from the file itself.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="PixelWidth">Intrinsic width in pixels.</param>
/// <param name="PixelHeight">Intrinsic height in pixels.</param>
/// <param name="X">Left edge on the canvas, a multiple of the 20 px grid. Unused for attached images.</param>
/// <param name="Y">Top edge on the canvas, a multiple of the 20 px grid. Unused for attached images.</param>
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
        new(image.Id, image.BoardId, image.NoteId, image.FileName, image.ContentType, image.Size, image.PixelWidth,
            image.PixelHeight, image.X, image.Y, image.Width, image.Height, image.Caption, image.AddedBy?.ShownName ?? "Unknown", image.AddedById,
            $"/api/images/{image.Id}/content", image.CreatedAt, image.UpdatedAt);
}

/// <summary>Moves, resizes and/or re-captions an image. Omitted (null) fields stay unchanged.</summary>
/// <param name="X">New left edge; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge; snapped to the 20 px grid.</param>
/// <param name="Width">New display width; snapped to the grid, between 80 and 4000. The height follows the aspect ratio.</param>
/// <param name="Caption">New caption; an empty string clears it.</param>
public record UpdateImageRequest(
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int? Width = null,
    [property: StringLength(500)] string? Caption = null);
