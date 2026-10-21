namespace PrikBoard.Api.Domain;

/// <summary>
/// A tag defined on a board. Cards pick their tags from the board's list (by name, see <see cref="Card.Tags"/>); a tag
/// stays on the board when no card uses it. Renaming or deleting one changes every card on the board.
/// </summary>
public class BoardTag
{
    public const int MaxPerBoard = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    /// <summary>Normalized, see <see cref="CardTags.Normalize(string)"/>; unique on the board.</summary>
    public required string Name { get; set; }
    /// <summary>How the tag looks on cards; <see cref="TagColor.Default"/> follows the card's fill.</summary>
    public TagColor Color { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>While deleted: the cards it was removed from, so restoring puts it back on them.</summary>
    public List<Guid>? RemovedFromCardIds { get; set; }
}
