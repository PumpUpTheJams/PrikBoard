namespace PrikBoard.Api.Domain;

/// <summary>A project board: a freeform canvas holding buckets and post-it notes.</summary>
public class Board
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Bucket> Buckets { get; set; } = [];
    public List<Note> Notes { get; set; } = [];

    public static readonly (string Name, BucketColor Color)[] DefaultBuckets =
        [("To do", BucketColor.Blue), ("Doing", BucketColor.Amber), ("Done", BucketColor.Teal)];
}
