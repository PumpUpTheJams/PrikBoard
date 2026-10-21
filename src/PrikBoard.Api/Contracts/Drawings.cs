using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A freehand pen stroke, on the board or on a card's canvas. The board snapshot carries only its place, size and color;
/// fetch the points from contentUrl (getDrawingContent).
/// </summary>
/// <param name="Id">Drawing identifier.</param>
/// <param name="BoardId">Board the drawing belongs to.</param>
/// <param name="CardId">Card the drawing is on (on the card's own canvas, shown when the card is expanded), or null when it sits on the board.</param>
/// <param name="BucketId">Bucket the drawing sits in on the board (the topmost one containing its centre when it was put there), which carries it along when it moves; null when it is loose or on a card.</param>
/// <param name="X">Left edge, a multiple of the 20 px grid: on the board, or on the card's canvas. The points are relative to it.</param>
/// <param name="Y">Top edge, a multiple of the 20 px grid: on the board, or on the card's canvas. The points are relative to it.</param>
/// <param name="Width">Width of the box from x that holds every point plus half the stroke width, in canvas pixels.</param>
/// <param name="Height">Height of the box from y that holds every point plus half the stroke width, in canvas pixels.</param>
/// <param name="Color">Ink: light Chalk, Butter, Mint, Sky, Coral or Lilac (for the dark board), or dark Ink, Ochre, Pine, Ocean, Brick or Violet (for a card's light fill).</param>
/// <param name="StrokeWidth">Base line width in pixels: 2, 4 or 8. Pressure makes the line thinner or thicker around it.</param>
/// <param name="PointCount">How many points the stroke has.</param>
/// <param name="AddedBy">Current display name of whoever drew it, or "Unknown" once they no longer exist.</param>
/// <param name="AddedById">Principal id of whoever drew it, or null once they no longer exist.</param>
/// <param name="ContentUrl">Where to fetch the points (getDrawingContent). Needs the same authentication as the API.</param>
/// <param name="CreatedAt">When it was drawn (UTC).</param>
/// <param name="UpdatedAt">When it was last moved or recolored (UTC).</param>
public record DrawingDetail(
    Guid Id,
    Guid BoardId,
    Guid? CardId,
    Guid? BucketId,
    int X,
    int Y,
    int Width,
    int Height,
    InkColor Color,
    int StrokeWidth,
    int PointCount,
    string AddedBy,
    Guid? AddedById,
    string ContentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static DrawingDetail From(BoardDrawing drawing) =>
        new(drawing.Id, drawing.BoardId, drawing.CardId, drawing.BucketId, drawing.X, drawing.Y, drawing.Width, drawing.Height, drawing.Color,
            drawing.StrokeWidth, drawing.PointCount, drawing.AddedBy?.ShownName ?? "Unknown", drawing.AddedById,
            $"/api/drawings/{drawing.Id}/content", drawing.CreatedAt, drawing.UpdatedAt);
}

/// <summary>The points of a drawing. They never change after it is made, so they can be cached.</summary>
/// <param name="DrawingId">The drawing they belong to.</param>
/// <param name="Points">The stroke in drawing order, each point [x, y, pressure]: x and y in canvas pixels from the drawing's x and y (to one decimal), pressure from 0 to 1 (to two decimals).</param>
public record DrawingContentDetail(Guid DrawingId, IReadOnlyList<double[]> Points);

/// <summary>
/// A new freehand stroke. Give its top-left (x, y; snapped to the 20 px grid) and the points relative to it. Points are
/// not snapped, so send x and y on the grid and the points measured from there to keep the line exactly where it was drawn.
/// Leave about half the stroke width between the points and x/y, or the edge of the line may be cut off.
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
/// Moves or recolors a drawing, or moves it between the board and a card. Omitted (null) fields stay unchanged. X and Y
/// are on wherever the drawing ends up: the board, or the card's canvas; give both when attaching or detaching. The points
/// themselves never change.
/// </summary>
/// <param name="X">New left edge; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge; snapped to the 20 px grid.</param>
/// <param name="Color">New ink.</param>
/// <param name="CardId">Put the drawing on this card's canvas (on the same board), from the board or from another card.</param>
/// <param name="Detach">True to take a drawing off its card and put it on the board. Do not combine with cardId.</param>
public record UpdateDrawingRequest(
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    InkColor? Color = null,
    Guid? CardId = null,
    bool? Detach = null);
