namespace PrikBoard.Api.Domain;

/// <summary>
/// One freehand pen stroke. Without a <see cref="CardId"/> it sits on the board at <see cref="X"/>, <see cref="Y"/>; with
/// one it is drawn on that card's own canvas. The points live in <see cref="DrawingContent"/>, relative to X and Y, so
/// board snapshots carry only the place, size and color.
/// </summary>
public class BoardDrawing : ICanvasItem
{
    /// <summary>Most points one stroke can have.</summary>
    public const int MaxPoints = 5000;
    /// <summary>Most drawings a board can hold, on its canvas and on its cards together.</summary>
    public const int MaxPerBoard = 5000;
    /// <summary>Points run from 0 to this, in both directions, from the drawing's X and Y.</summary>
    public const int MaxSize = Layout.CardCanvasMax;
    /// <summary>The base line widths a stroke can have; pressure makes the line thinner or thicker around it.</summary>
    public static readonly int[] StrokeWidths = [2, 4, 8];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public Guid? CardId { get; set; }
    /// <summary>
    /// The bucket the drawing sits in on the board (it moves along with it), or null when it is loose or on a card.
    /// Maintained by <see cref="Layout"/>, like <see cref="Card.BucketId"/>.
    /// </summary>
    public Guid? BucketId { get; set; }
    /// <summary>Top-left, snapped to the grid: on the board, or on the card's canvas when attached.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>The box from X and Y that holds every point plus half the line width, see <see cref="SetPoints"/>.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public InkColor Color { get; set; }
    public int StrokeWidth { get; set; }
    public int PointCount { get; set; }
    /// <summary>The user or agent that drew it; null once they no longer exist.</summary>
    public Guid? AddedById { get; set; }
    public Principal? AddedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Deleted with its card, it shares the card's stamp. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public DrawingContent? Content { get; set; }

    /// <summary>
    /// Checks <paramref name="points"/> (each <c>[x, y, pressure]</c>, x and y from 0 to <see cref="MaxSize"/>, pressure
    /// from 0 to 1) and stores them rounded, with the size and point count that follow. Returns the problem with the first
    /// bad point as (index, message), or null when they are fine.
    /// </summary>
    public (int Index, string Message)? SetPoints(IReadOnlyList<double[]> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point is not { Length: 3 }) return (i, "A point is [x, y, pressure].");
            if (!point.All(double.IsFinite)) return (i, "Point values must be finite numbers.");
            if (point[0] is < 0 or > MaxSize || point[1] is < 0 or > MaxSize) return (i, $"x and y run from 0 to {MaxSize}, from the drawing's x and y.");
            if (point[2] is < 0 or > 1) return (i, "Pressure runs from 0 to 1.");
        }

        // A tenth of a pixel and a hundredth of pressure is finer than any screen shows, and keeps the content small.
        var rounded = points.Select(p => new[] { Math.Round(p[0], 1), Math.Round(p[1], 1), Math.Round(p[2], 2) }).ToArray();
        var margin = (StrokeWidth + 1) / 2;
        Width = (int)Math.Ceiling(rounded.Max(p => p[0])) + margin;
        Height = (int)Math.Ceiling(rounded.Max(p => p[1])) + margin;
        PointCount = rounded.Length;
        Content = new DrawingContent { Points = rounded };
        return null;
    }
}

/// <summary>The points of a <see cref="BoardDrawing"/>, in their own table so board snapshots never load them.</summary>
public class DrawingContent
{
    public Guid DrawingId { get; set; }
    /// <summary>Each point is <c>[x, y, pressure]</c>, relative to the drawing's X and Y.</summary>
    public required double[][] Points { get; set; }
}
