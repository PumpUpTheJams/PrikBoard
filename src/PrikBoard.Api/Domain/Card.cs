namespace PrikBoard.Api.Domain;

/// <summary>A card: one unit of work on a board, inside a bucket or loose.</summary>
public class Card
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    /// <summary>
    /// The card's number on its board, from <see cref="Domain.Board.NextCardNumber"/>: 1, 2, 3 and so on, never reused. Shown with
    /// the board's key (MPCT-415) and part of its path (<see cref="CardLinks.CardPath(string, int)"/>).
    /// </summary>
    public int Number { get; set; }
    /// <summary>
    /// The bucket the card is in, or null when it is loose: the one it joined when it last moved, kept while buckets
    /// move around it. Maintained by <see cref="Layout"/>.
    /// </summary>
    public Guid? BucketId { get; set; }
    public required string Title { get; set; }
    public CardColor Color { get; set; } = CardColor.Butter;
    /// <summary>Names of the board's tags (<see cref="BoardTag"/>) on this card, see <see cref="CardTags.Normalize(string)"/>.</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>
    /// Principal ids of the people and agents on the card, in display order. Added ones must be able to reach the board
    /// (<see cref="Auth.BoardAccess.AssignableIds"/>); ones who later lose access stay.
    /// </summary>
    public List<Guid> AssigneeIds { get; set; } = [];
    /// <summary>The card's content (text, checklists, links) laid out on its own canvas; see <see cref="CardComponent"/>.</summary>
    public List<CardComponent> Components { get; set; } = [];
    /// <summary>Set while an AI agent is actively working on the card; the board shows a halo.</summary>
    public bool AgentWorking { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>The user or agent that added the card to the board; null for older cards, or once they no longer exist.</summary>
    public Guid? CreatedById { get; set; }
    public Principal? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
