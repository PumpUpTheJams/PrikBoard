namespace PrikBoard.Api.Domain;

/// <summary>
/// An image or GIF on a board. Without a <see cref="NoteId"/> it sits on the canvas at <see cref="X"/>, <see cref="Y"/>;
/// with one it is attached to that note and shown when the note is expanded. The bytes live in <see cref="ImageContent"/>.
/// </summary>
public class BoardImage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public Guid? NoteId { get; set; }
    /// <summary>
    /// The bucket the image sits in on the board (it moves along with it), or null when it is loose or attached to a
    /// note. Maintained by <see cref="Layout"/>, like <see cref="Note.BucketId"/>.
    /// </summary>
    public Guid? BucketId { get; set; }
    public required string FileName { get; set; }
    /// <summary>Detected from the file itself, see <see cref="ImageFormats"/>.</summary>
    public required string ContentType { get; set; }
    public long Size { get; set; }
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
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Deleted with its note, it shares the note's stamp. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public ImageContent? Content { get; set; }

    /// <summary>Sets the display width (snapped to the grid and kept within limits) and the matching height.</summary>
    public void Resize(int width)
    {
        Width = Math.Clamp(Layout.Snap(width), Layout.MinImageWidth, Layout.MaxImageWidth);
        Height = Math.Max(1, (int)Math.Round(Width * (double)PixelHeight / PixelWidth));
    }
}

/// <summary>The bytes of a <see cref="BoardImage"/>, in their own table so board snapshots never load them.</summary>
public class ImageContent
{
    public Guid ImageId { get; set; }
    public required byte[] Data { get; set; }
}
