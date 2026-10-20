namespace PrikBoard.Api.Domain;

/// <summary>
/// Something besides a note that sits on the board, or on a note's canvas when <see cref="NoteId"/> is set: an image or a
/// drawing. On the board it joins buckets by its centre, like a note (see <see cref="Layout"/>).
/// </summary>
public interface ICanvasItem
{
    Guid? NoteId { get; }
    /// <summary>The bucket it sits in on the board (it moves along with it), or null when loose or on a note.</summary>
    Guid? BucketId { get; set; }
    int X { get; set; }
    int Y { get; set; }
    int Width { get; }
    int Height { get; }
}
