namespace PrikBoard.Api.Domain;

/// <summary>A vertical lane on a board, e.g. "To do" or "Done".</summary>
public class Column : IPositioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }

    public List<Note> Notes { get; set; } = [];
}
