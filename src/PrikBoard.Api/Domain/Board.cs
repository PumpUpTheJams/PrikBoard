namespace PrikBoard.Api.Domain;

/// <summary>A project board holding ordered columns of post-it notes.</summary>
public class Board
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Column> Columns { get; set; } = [];

    public static readonly string[] DefaultColumnNames = ["To do", "Doing", "Done"];
}
