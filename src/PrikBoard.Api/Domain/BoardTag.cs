namespace PrikBoard.Api.Domain;

/// <summary>
/// A tag defined on a board. Notes pick their tags from the board's list (by name, see <see cref="Note.Tags"/>); a tag
/// stays on the board when no note uses it. Renaming or deleting one changes every note on the board.
/// </summary>
public class BoardTag
{
    public const int MaxPerBoard = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    /// <summary>Normalized, see <see cref="NoteTags.Normalize(string)"/>; unique on the board.</summary>
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
