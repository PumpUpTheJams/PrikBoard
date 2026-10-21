namespace PrikBoard.Api.Domain;

/// <summary>
/// An image or GIF on a board. Without a <see cref="CardId"/> it sits on the canvas at <see cref="X"/>, <see cref="Y"/>;
/// with one it is attached to that card and shown when the card is expanded. The file itself is <see cref="File"/>.
/// </summary>
public class BoardImage : ICanvasItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public Guid? CardId { get; set; }
    /// <summary>
    /// The bucket the image sits in on the board (it moves along with it), or null when it is loose or attached to a
    /// card. Maintained by <see cref="Layout"/>, like <see cref="Card.BucketId"/>.
    /// </summary>
    public Guid? BucketId { get; set; }
    /// <summary>Intrinsic size of the image in pixels.</summary>
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>Display size on the board; the height always follows from the width and the aspect ratio.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Caption { get; set; }
    /// <summary>The user or agent that added the image; null once they no longer exist.</summary>
    public Guid? AddedById { get; set; }
    public Principal? AddedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Deleted with its card, it shares the card's stamp. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>The image file: its name, content type and size, and where the bytes are on disk.</summary>
    public Guid FileId { get; set; }
    public StoredFile? File { get; set; }

    /// <summary>Sets the display width (snapped to the grid and kept within limits) and the matching height.</summary>
    public void Resize(int width)
    {
        Width = Math.Clamp(Layout.Snap(width), Layout.MinImageWidth, Layout.MaxImageWidth);
        Height = Math.Max(1, (int)Math.Round(Width * (double)PixelHeight / PixelWidth));
    }
}
