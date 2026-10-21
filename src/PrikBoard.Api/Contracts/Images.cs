using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>An image or GIF on a board, either on the canvas or attached to a card.</summary>
/// <param name="Id">Image identifier.</param>
/// <param name="BoardId">Board the image belongs to.</param>
/// <param name="CardId">Card the image is attached to (on the card's own canvas, shown when the card is expanded), or null when it sits on the board.</param>
/// <param name="BucketId">Bucket the image sits in on the board, which carries it along when it moves; null when it is loose or attached to a card.</param>
/// <param name="FileName">Original file name, e.g. "ingest-errors.png".</param>
/// <param name="ContentType">image/png, image/jpeg, image/gif or image/webp, detected from the file itself.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="PixelWidth">Intrinsic width in pixels.</param>
/// <param name="PixelHeight">Intrinsic height in pixels.</param>
/// <param name="X">Left edge, a multiple of the 20 px grid: on the board, or on the card's canvas when attached.</param>
/// <param name="Y">Top edge, a multiple of the 20 px grid: on the board, or on the card's canvas when attached.</param>
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
    Guid? CardId,
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
        new(image.Id, image.BoardId, image.CardId, image.BucketId, image.File!.FileName, image.File.ContentType, image.File.Size, image.PixelWidth,
            image.PixelHeight, image.X, image.Y, image.Width, image.Height, image.Caption, image.AddedBy?.ShownName ?? "Unknown", image.AddedById,
            $"/api/images/{image.Id}/content", image.CreatedAt, image.UpdatedAt);
}

/// <summary>
/// Moves, resizes and/or re-captions an image, or moves it between the board and a card. Omitted (null) fields stay
/// unchanged. X and Y are on wherever the image ends up: the board, or the card's canvas. An image that changes place
/// without X and Y goes in a free spot there.
/// </summary>
/// <param name="X">New left edge; snapped to the 20 px grid. Give it together with Y when attaching or detaching.</param>
/// <param name="Y">New top edge; snapped to the 20 px grid. Give it together with X when attaching or detaching.</param>
/// <param name="Width">New display width; snapped to the grid, between 80 and 4000. The height follows the aspect ratio.</param>
/// <param name="Caption">New caption; an empty string clears it.</param>
/// <param name="CardId">Attach the image to this card on the same board (from the board, or from another card).</param>
/// <param name="Detach">True to take an attached image off its card and put it on the board. Do not combine with cardId.</param>
public record UpdateImageRequest(
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int? Width = null,
    [property: StringLength(500)] string? Caption = null,
    Guid? CardId = null,
    bool? Detach = null);

/// <summary>
/// Asks for a one-time upload URL (createImageUpload): where the image goes once its file is sent there. The fields are
/// those of uploadImage's query parameters; they are checked now and again on upload.
/// </summary>
/// <param name="CardId">Attach the image to this card on the board, instead of putting it on the board.</param>
/// <param name="X">Left edge on the board, or on the card's canvas with cardId. Give it together with Y; default: a free spot.</param>
/// <param name="Y">Top edge on the board, or on the card's canvas with cardId. Give it together with X.</param>
/// <param name="Width">Display width, 80 to 4000. Default: the image's own width, at most 360.</param>
/// <param name="Caption">Caption shown under the image.</param>
/// <param name="FileName">File name, e.g. "ingest-errors.png". Default: image.&lt;format&gt;.</param>
public record CreateImageUploadRequest(
    Guid? CardId = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinImageWidth, Layout.MaxImageWidth)] int? Width = null,
    [property: StringLength(500)] string? Caption = null,
    [property: StringLength(255)] string? FileName = null);

/// <summary>A one-time upload URL for an image.</summary>
/// <param name="UploadUrl">Send the file here with a PUT, the raw bytes as the body (no form encoding, no authentication: the URL itself is the key, so keep it private). The answer is the new image (201).</param>
/// <param name="ExpiresAt">When the URL stops working (UTC). It adds one image; a failed upload (not an image, too large) can be retried with another file.</param>
/// <param name="Command">A command that uploads a file there, with FILE in place of its path.</param>
public record ImageUploadUrl(string UploadUrl, DateTimeOffset ExpiresAt, string Command);
