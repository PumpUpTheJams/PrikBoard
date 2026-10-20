namespace PrikBoard.Api.Domain;

/// <summary>A project board: a freeform canvas holding buckets, post-it notes, images and drawings.</summary>
public class Board
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Markdown written by the owners: what the board is about and how agents should handle its notes.</summary>
    public string? Rules { get; set; }
    /// <summary>When <see cref="Rules"/> last changed, or null if they were never set.</summary>
    public DateTimeOffset? RulesUpdatedAt { get; set; }

    public List<Bucket> Buckets { get; set; } = [];
    public List<Note> Notes { get; set; } = [];
    /// <summary>Images on the canvas and images attached to notes.</summary>
    public List<BoardImage> Images { get; set; } = [];
    /// <summary>Freehand pen strokes on the canvas and on notes.</summary>
    public List<BoardDrawing> Drawings { get; set; } = [];
    /// <summary>The tags notes on this board can have.</summary>
    public List<BoardTag> Tags { get; set; } = [];
    /// <summary>Who may see and change the board; there is always at least one owner.</summary>
    public List<BoardMember> Members { get; set; } = [];

    /// <summary>The longest agent rules a board can have, in characters.</summary>
    public const int RulesMaxLength = 20000;

    public static readonly (string Name, BucketColor Color)[] DefaultBuckets =
        [("To do", BucketColor.Blue), ("Doing", BucketColor.Amber), ("Done", BucketColor.Teal)];
}
