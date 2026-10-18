namespace PrikBoard.Api.Domain;

/// <summary>A rectangle drawn on a board, e.g. "To do" or "Done". Post-its whose centre lies inside it belong to it.</summary>
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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}
