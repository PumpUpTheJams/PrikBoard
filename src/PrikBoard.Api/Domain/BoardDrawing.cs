namespace PrikBoard.Api.Domain;

/// <summary>
/// A freehand drawing: one or more pen strokes that select, move and delete as one. Everything drawn between picking up
/// the pen and putting it down goes into one drawing. Without a <see cref="CardId"/> it sits on the board at
/// <see cref="X"/>, <see cref="Y"/>; with one it is drawn on that card's own canvas. The strokes live in
/// <see cref="DrawingContent"/>, relative to X and Y, so board snapshots carry only the place, size and inks.
/// </summary>
public class BoardDrawing : ICanvasItem
{
    /// <summary>Most points one stroke can have.</summary>
    public const int MaxPoints = 5000;
    /// <summary>Most points one drawing can have, over all its strokes.</summary>
    public const int MaxTotalPoints = 50_000;
    /// <summary>Most strokes one drawing can have.</summary>
    public const int MaxStrokes = 1000;
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
    /// <summary>The box from X and Y that holds every point plus half the line width, see <see cref="Measure"/>.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The ink of each stroke, in drawing order (the strokes themselves are in <see cref="Content"/>).</summary>
    public List<InkColor> Colors { get; set; } = [];
    public int PointCount { get; set; }
    /// <summary>Goes up whenever the strokes change, so the content's URL changes with it and caches stay right.</summary>
    public int Revision { get; set; }
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
    /// from 0 to 1) and returns them rounded, or the problem with the first bad point as (index, message).
    /// </summary>
    public static (double[][]? Points, (int Index, string Message)? Problem) CheckPoints(IReadOnlyList<double[]> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point is not { Length: 3 }) return (null, (i, "A point is [x, y, pressure]."));
            if (!double.IsFinite(point[0]) || !double.IsFinite(point[1]) || !double.IsFinite(point[2]))
            {
                return (null, (i, "Point values must be finite numbers."));
            }
            if (point[0] is < 0 or > MaxSize || point[1] is < 0 or > MaxSize) return (null, (i, $"x and y run from 0 to {MaxSize}, from the drawing's x and y."));
            if (point[2] is < 0 or > 1) return (null, (i, "Pressure runs from 0 to 1."));
        }
        // A tenth of a pixel and a hundredth of pressure is finer than any screen shows, and keeps the content small.
        return (points.Select(p => new[] { Math.Round(p[0], 1), Math.Round(p[1], 1), Math.Round(p[2], 2) }).ToArray(), null);
    }

    /// <summary>The strokes, in drawing order; empty before the content is set.</summary>
    public IReadOnlyList<DrawingStroke> Strokes => Content?.Strokes ?? [];

    /// <summary>Sets the strokes and their inks (as many as strokes), with the size and point count that follow.</summary>
    public void SetStrokes(IReadOnlyList<DrawingStroke> strokes, IReadOnlyList<InkColor> colors)
    {
        if (Content is null) Content = new DrawingContent { Strokes = [.. strokes] };
        else Content.Strokes = [.. strokes];
        Colors = [.. colors];
        Measure();
    }

    /// <summary>
    /// Adds a stroke whose points are measured from (<paramref name="x"/>, <paramref name="y"/>) on the drawing's canvas.
    /// The drawing grows to hold it: when it starts above or left of the drawing, the corner moves there (both are on the
    /// grid) and the other strokes' points shift by as much. Returns false when the drawing would get bigger than
    /// <see cref="MaxSize"/>.
    /// </summary>
    public bool AddStroke(DrawingStroke stroke, InkColor color, int x, int y)
    {
        var (left, top) = (Math.Min(X, x), Math.Min(Y, y));
        var strokes = Strokes.Select(s => s.Shifted(X - left, Y - top)).Append(stroke.Shifted(x - left, y - top)).ToList();
        // The others only moved when the corner did; unmoved, they still fit.
        var moved = X != left || Y != top;
        if ((moved ? strokes : [strokes[^1]]).Exists(s => !s.Fits())) return false;
        (X, Y) = (left, top);
        SetStrokes(strokes, [.. Colors, color]);
        Revision++;
        return true;
    }

    /// <summary>
    /// Takes away the stroke at <paramref name="index"/>; the corner then moves in as far as the others allow (on the grid,
    /// half the thickest line clear of each, as the web app places a new drawing), so taking away the stroke just added
    /// gives back the drawing as it was.
    /// </summary>
    public void RemoveStroke(int index)
    {
        var strokes = Strokes.Where((_, i) => i != index).ToList();
        var colors = Colors.Where((_, i) => i != index).ToList();
        var dx = Math.Max(0, strokes.Min(s => s.Inset(p => p[0])));
        var dy = Math.Max(0, strokes.Min(s => s.Inset(p => p[1])));
        (X, Y) = (X + dx, Y + dy);
        SetStrokes([.. strokes.Select(s => s.Shifted(-dx, -dy))], colors);
        Revision++;
    }

    /// <summary>
    /// Scales the drawing by (<paramref name="scaleX"/>, <paramref name="scaleY"/>) and then turns it
    /// <paramref name="degrees"/> clockwise, both about (<paramref name="pivotX"/>, <paramref name="pivotY"/>) on its canvas.
    /// The points themselves change (the line widths don't), and the corner moves to the last grid line half the thickest
    /// line clear of them, as for a new drawing, so the lines stay exactly where the transform puts them. Returns the
    /// problem when the result would be bigger than <see cref="MaxSize"/> or leave the board, and changes nothing then.
    /// </summary>
    public string? Transform(double scaleX, double scaleY, double degrees, double pivotX, double pivotY)
    {
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var strokes = Strokes.Select(s => s with
        {
            Points = [.. s.Points.Select(p =>
            {
                var dx = (X + p[0] - pivotX) * scaleX;
                var dy = (Y + p[1] - pivotY) * scaleY;
                return new[] { pivotX + dx * cos - dy * sin, pivotY + dx * sin + dy * cos, p[2] };
            })],
        }).ToList();
        var left = strokes.Min(s => s.Inset(p => p[0]));
        var top = strokes.Min(s => s.Inset(p => p[1]));
        // Measured from the new corner; the points are new arrays, so this changes nothing else.
        foreach (var point in strokes.SelectMany(s => s.Points))
        {
            (point[0], point[1]) = (Math.Round(point[0] - left, 1), Math.Round(point[1] - top, 1));
        }
        if (strokes.Exists(s => !s.Fits())) return $"A drawing reaches at most {MaxSize} px from its corner.";
        if (left < Layout.MinCoordinate || top < Layout.MinCoordinate || left > Layout.MaxCoordinate || top > Layout.MaxCoordinate)
        {
            return "The drawing would leave the board.";
        }
        (X, Y) = (left, top);
        SetStrokes(strokes, Colors);
        Revision++;
        return null;
    }

    /// <summary>Works out the box and point count from the strokes.</summary>
    private void Measure()
    {
        (Width, Height, PointCount) = (int.MinValue, int.MinValue, 0);
        foreach (var stroke in Strokes)
        {
            var (right, bottom) = (double.MinValue, double.MinValue);
            foreach (var point in stroke.Points) (right, bottom) = (Math.Max(right, point[0]), Math.Max(bottom, point[1]));
            var margin = (stroke.StrokeWidth + 1) / 2;
            Width = Math.Max(Width, (int)Math.Ceiling(right) + margin);
            Height = Math.Max(Height, (int)Math.Ceiling(bottom) + margin);
            PointCount += stroke.Points.Length;
        }
    }
}

/// <summary>One pen stroke of a <see cref="BoardDrawing"/>: its base width and its points.</summary>
/// <param name="StrokeWidth">Base line width: 2, 4 or 8.</param>
/// <param name="Points">Each point is <c>[x, y, pressure]</c>, relative to the drawing's X and Y.</param>
public record DrawingStroke(int StrokeWidth, double[][] Points)
{
    /// <summary>The same stroke with its points moved by (dx, dy).</summary>
    public DrawingStroke Shifted(int dx, int dy) =>
        dx == 0 && dy == 0 ? this : this with { Points = [.. Points.Select(p => new[] { Math.Round(p[0] + dx, 1), Math.Round(p[1] + dy, 1), p[2] })] };

    /// <summary>Whether every point is within <see cref="BoardDrawing.MaxSize"/> of the drawing's corner.</summary>
    public bool Fits()
    {
        foreach (var point in Points)
        {
            if (point[0] > BoardDrawing.MaxSize || point[1] > BoardDrawing.MaxSize) return false;
        }
        return true;
    }

    /// <summary>
    /// How far a drawing of only this stroke would have its corner from 0 on one axis: the last grid line at least half
    /// the line at full pressure (0.8 × the base width) before its first point.
    /// </summary>
    public int Inset(Func<double[], double> axis) =>
        (int)Math.Floor((Points.Min(axis) - StrokeWidth * 0.8) / Layout.Grid) * Layout.Grid;
}

/// <summary>The strokes of a <see cref="BoardDrawing"/>, in their own table so board snapshots never load them.</summary>
public class DrawingContent
{
    public Guid DrawingId { get; set; }
    /// <summary>The strokes in drawing order, their points relative to the drawing's X and Y.</summary>
    public required List<DrawingStroke> Strokes { get; set; }
}
