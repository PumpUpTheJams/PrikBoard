namespace PrikBoard.Api.Domain;

/// <summary>A post-it note: one unit of work living in a column.</summary>
public class Note : IPositioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ColumnId { get; set; }
    public Column? Column { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public NoteColor Color { get; set; } = NoteColor.Yellow;
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
