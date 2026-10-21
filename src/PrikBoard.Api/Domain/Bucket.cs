namespace PrikBoard.Api.Domain;

/// <summary>A rectangle drawn on a board, e.g. "To do" or "Done". Cards whose centre lies inside it belong to it.</summary>
public class Bucket
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public required string Name { get; set; }
    public BucketColor Color { get; set; } = BucketColor.Blue;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>
    /// Folded up into a square (<see cref="Layout.CollapsedBucketSize"/>) in its top-right corner: its cards are hidden,
    /// keeping their places for when it opens again. Cards elsewhere don't join it while it is collapsed.
    /// </summary>
    public bool Collapsed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}
