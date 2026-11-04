namespace PrikBoard.Api.Domain;

/// <summary>
/// A note as it was when <see cref="Data.NoteMigration"/> turned it into a label: notes were Markdown in a card's components, with
/// a title and a fixed size. Kept to put right anything the move lost, and it goes with its card.
/// </summary>
public class NoteBackup
{
    /// <summary>The note's own component id.</summary>
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public Guid BoardId { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The label it became; null for a note with no text, which became nothing.</summary>
    public Guid? LabelId { get; set; }
    public DateTimeOffset MigratedAt { get; set; } = DateTimeOffset.UtcNow;
}
