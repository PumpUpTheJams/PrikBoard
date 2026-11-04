namespace PrikBoard.Api.Domain;

/// <summary>
/// Something besides a card that sits on the board, or on a card's canvas when <see cref="CardId"/> is set: an image, a
/// drawing or a label. On the board it joins buckets by its center, like a card (see <see cref="Layout"/>).
/// </summary>
public interface ICanvasItem
{
    Guid Id { get; }
    Guid BoardId { get; }
    Guid? CardId { get; set; }
    /// <summary>When it was deleted; hidden by the query filter while set.</summary>
    DateTimeOffset? DeletedAt { get; set; }
    /// <summary>The bucket it sits in on the board (it moves along with it), or null when loose or on a card.</summary>
    Guid? BucketId { get; set; }
    int X { get; set; }
    int Y { get; set; }
    int Width { get; }
    int Height { get; }
}
