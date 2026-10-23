using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A freehand drawing on the board or on a card's canvas: one or more pen strokes that select, move and delete as one
/// (everything drawn between picking up the pen and putting it down). The board snapshot carries only its place, size and
/// inks; fetch the strokes from contentUrl (getDrawingContent).
/// </summary>
/// <param name="Id">Drawing identifier.</param>
/// <param name="BoardId">Board the drawing belongs to.</param>
/// <param name="CardId">Card the drawing is on (on the card's own canvas, shown when the card is expanded), or null when it sits on the board.</param>
/// <param name="BucketId">Bucket the drawing belongs to on the board (the topmost one containing its center when it was put there); it moves along with that bucket. Null when it is loose or on a card.</param>
/// <param name="X">Left edge, a multiple of the 20 px grid: on the board, or on the card's canvas. The points are relative to it.</param>
/// <param name="Y">Top edge, a multiple of the 20 px grid: on the board, or on the card's canvas. The points are relative to it.</param>
/// <param name="Width">Width of the box from x that holds every point plus half the stroke width, in canvas pixels.</param>
/// <param name="Height">Height of the box from y that holds every point plus half the stroke width, in canvas pixels.</param>
/// <param name="Colors">The ink of each stroke, in drawing order (as many as strokes): light Chalk, Butter, Mint, Sky, Coral or Lilac (for the dark board), or dark Ink, Ochre, Pine, Ocean, Brick or Violet (for a card's light fill).</param>
/// <param name="PointCount">How many points its strokes have together.</param>
/// <param name="AddedBy">Current display name of whoever drew it, or "Unknown" once they no longer exist.</param>
/// <param name="AddedById">Principal id of whoever drew it, or null once they no longer exist.</param>
/// <param name="ContentUrl">Where to fetch the strokes (getDrawingContent). It changes whenever the strokes do. Needs the same authentication as the API.</param>
/// <param name="CreatedAt">When it was drawn (UTC).</param>
/// <param name="UpdatedAt">When its strokes, place or inks last changed (UTC).</param>
/// <param name="StackedAt">For a loose drawing (no bucketId or cardId): its place in the stack with the buckets, the same for everyone. The later, the higher; buckets and loose drawings are drawn in stackedAt order, each bucket with what is in it. Drawing, moving or reshaping it puts it on top, and so does bringDrawingToFront.</param>
public record DrawingDetail(
    Guid Id,
    Guid BoardId,
    Guid? CardId,
    Guid? BucketId,
    int X,
    int Y,
    int Width,
    int Height,
    IReadOnlyList<InkColor> Colors,
    int PointCount,
    string AddedBy,
    Guid? AddedById,
    string ContentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset StackedAt)
{
    public static DrawingDetail From(BoardDrawing drawing) =>
        new(drawing.Id, drawing.BoardId, drawing.CardId, drawing.BucketId, drawing.X, drawing.Y, drawing.Width, drawing.Height, drawing.Colors,
            drawing.PointCount, drawing.AddedBy?.ShownName ?? "Unknown", drawing.AddedById,
            $"/api/drawings/{drawing.Id}/content?v={drawing.Revision}", drawing.CreatedAt, drawing.UpdatedAt, drawing.StackedAt);
}

/// <summary>One stroke of a drawing.</summary>
/// <param name="StrokeWidth">Base line width in pixels: 2, 4 or 8. Pressure makes the line thinner or thicker around it.</param>
/// <param name="Points">The stroke in drawing order, each point [x, y, pressure]: x and y in canvas pixels from the drawing's x and y (to one decimal), pressure from 0 to 1 (to two decimals).</param>
public record DrawingStrokeDetail(int StrokeWidth, IReadOnlyList<double[]> Points);

/// <summary>The strokes of a drawing, in drawing order; their inks are the drawing's colors. A content URL never changes what it returns, so it can be cached.</summary>
/// <param name="DrawingId">The drawing they belong to.</param>
/// <param name="Strokes">The strokes in drawing order.</param>
public record DrawingContentDetail(Guid DrawingId, IReadOnlyList<DrawingStrokeDetail> Strokes)
{
    public static DrawingContentDetail From(Guid drawingId, IEnumerable<DrawingStroke> strokes) =>
        new(drawingId, [.. strokes.Select(s => new DrawingStrokeDetail(s.StrokeWidth, s.Points))]);
}

/// <summary>
/// A new freehand drawing of one stroke; add more strokes to it with addDrawingStroke. Give its top-left (x, y; snapped
/// to the 20 px grid) and the points relative to it. Points are not snapped, so send x and y on the grid and the points
/// measured from there to keep the line exactly where it was drawn. Leave about half the stroke width between the points
/// and x/y, or the edge of the line may be cut off.
/// </summary>
/// <param name="X">Left edge: on the board (-100000 to 100000), or on the card's canvas with cardId. Snapped to the grid.</param>
/// <param name="Y">Top edge: on the board (-100000 to 100000), or on the card's canvas with cardId. Snapped to the grid.</param>
/// <param name="Points">1 to 5000 points in drawing order, each [x, y, pressure]: x and y from 0 to 10000 relative to the drawing's x and y, pressure from 0 (light) to 1 (hard). Use 0.5 when there is no pressure, as with a mouse.</param>
/// <param name="Color">Ink; default Chalk. Use a light ink (Chalk, Butter, Mint, Sky, Coral, Lilac) on the board and a dark one (Ink, Ochre, Pine, Ocean, Brick, Violet) over a card.</param>
/// <param name="StrokeWidth">Base line width in pixels: 2 (fine), 4 (medium, the default) or 8 (bold).</param>
/// <param name="CardId">Draw on this card's own canvas (on the same board) instead of on the board.</param>
public record CreateDrawingRequest(
    [property: Required, Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X,
    [property: Required, Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y,
    [property: Required, MinLength(1), MaxLength(BoardDrawing.MaxPoints)] List<double[]> Points,
    InkColor Color = InkColor.Chalk,
    [property: AllowedValues(2, 4, 8)] int StrokeWidth = 4,
    Guid? CardId = null);

/// <summary>
/// A stroke to add to a drawing, like the first one in createDrawing: its own top-left (x, y; snapped to the 20 px grid)
/// on the drawing's canvas (the board, or the card's canvas) and the points relative to it. The drawing grows to hold it.
/// </summary>
/// <param name="X">Left edge of the stroke: on the board (-100000 to 100000), or on the card's canvas for a drawing on a card. Snapped to the grid.</param>
/// <param name="Y">Top edge of the stroke, like x.</param>
/// <param name="Points">1 to 5000 points in drawing order, each [x, y, pressure] relative to the stroke's x and y, as in createDrawing.</param>
/// <param name="Color">Ink; default Chalk.</param>
/// <param name="StrokeWidth">Base line width in pixels: 2 (fine), 4 (medium, the default) or 8 (bold).</param>
public record AddDrawingStrokeRequest(
    [property: Required, Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X,
    [property: Required, Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y,
    [property: Required, MinLength(1), MaxLength(BoardDrawing.MaxPoints)] List<double[]> Points,
    InkColor Color = InkColor.Chalk,
    [property: AllowedValues(2, 4, 8)] int StrokeWidth = 4);

/// <summary>
/// Scales and rotates a drawing about a pivot: first scaleX and scaleY, then rotation, both around the pivot (by default
/// the middle of its box). The strokes' points change, their line widths don't; the drawing's x, y, width and height
/// follow the new points.
/// </summary>
/// <param name="ScaleX">How much wider: 1 keeps the width, 2 doubles it, 0.5 halves it (0.05 to 20). Give scaleY the same value to keep its shape.</param>
/// <param name="ScaleY">How much taller, like scaleX.</param>
/// <param name="Rotation">Degrees to turn it clockwise (-360 to 360), after scaling.</param>
/// <param name="PivotX">The point it scales and turns around, on its canvas (the board, or the card's canvas); give pivotY with it. Default: the middle of its box.</param>
/// <param name="PivotY">Like pivotX.</param>
public record TransformDrawingRequest(
    [property: Range(0.05, 20)] double ScaleX = 1,
    [property: Range(0.05, 20)] double ScaleY = 1,
    [property: Range(-360, 360)] double Rotation = 0,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] double? PivotX = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] double? PivotY = null);

/// <summary>
/// New points for every stroke of a drawing, for example to put it back as it was before transformDrawing. Give its new
/// top-left (snapped to the 20 px grid) and the strokes in drawing order, as many as it has; their inks stay.
/// </summary>
/// <param name="X">New left edge, on the drawing's canvas; snapped to the grid.</param>
/// <param name="Y">New top edge, like x.</param>
/// <param name="Strokes">Every stroke in drawing order, each with its base width and its points relative to x and y, as in createDrawing.</param>
public record SetDrawingStrokesRequest(
    [property: Required, Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X,
    [property: Required, Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y,
    [property: Required, MinLength(1), MaxLength(BoardDrawing.MaxStrokes)] List<NewDrawingStroke> Strokes);

/// <summary>One stroke of a drawing to write.</summary>
/// <param name="StrokeWidth">Base line width in pixels: 2, 4 or 8.</param>
/// <param name="Points">1 to 5000 points in drawing order, each [x, y, pressure] relative to the drawing's x and y.</param>
public record NewDrawingStroke(
    [property: AllowedValues(2, 4, 8)] int StrokeWidth,
    [property: Required, MinLength(1), MaxLength(BoardDrawing.MaxPoints)] List<double[]> Points);

/// <summary>
/// Moves or recolors a drawing, or moves it between the board and a card. Omitted (null) fields stay unchanged. X and Y
/// are on wherever the drawing ends up: the board, or the card's canvas; give both when attaching or detaching. The points
/// themselves never change.
/// </summary>
/// <param name="X">New left edge; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge; snapped to the 20 px grid.</param>
/// <param name="Color">New ink for every stroke.</param>
/// <param name="Colors">New ink for each stroke, as many as it has (in drawing order). Do not combine with color.</param>
/// <param name="CardId">Put the drawing on this card's canvas (on the same board), from the board or from another card.</param>
/// <param name="Detach">True to take a drawing off its card and put it on the board. Do not combine with cardId.</param>
public record UpdateDrawingRequest(
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    InkColor? Color = null,
    [property: MaxLength(BoardDrawing.MaxStrokes)] List<InkColor>? Colors = null,
    Guid? CardId = null,
    bool? Detach = null);
