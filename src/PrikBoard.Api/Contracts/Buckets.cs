using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A bucket: a named rectangle on the board. Cards whose centre lies inside it belong to it.</summary>
/// <param name="Id">Bucket identifier.</param>
/// <param name="BoardId">Board the bucket belongs to.</param>
/// <param name="Name">Bucket name, e.g. "To do", "Doing" or "Done".</param>
/// <param name="Color">Outline color.</param>
/// <param name="X">Left edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Y">Top edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Width">Width in pixels, a multiple of 20 and at least 200.</param>
/// <param name="Height">Height in pixels, a multiple of 20 and at least 200.</param>
/// <param name="Collapsed">Folded up into a 120 px square in its top-right corner: its cards are hidden on the board (they keep their places, and still belong to it) until it is opened again.</param>
public record BucketDetail(Guid Id, Guid BoardId, string Name, BucketColor Color, int X, int Y, int Width, int Height, bool Collapsed)
{
    public static BucketDetail From(Bucket bucket) =>
        new(bucket.Id, bucket.BoardId, bucket.Name, bucket.Color, bucket.X, bucket.Y, bucket.Width, bucket.Height, bucket.Collapsed);
}

/// <summary>Draws a bucket on a board. Loose cards it covers join it. Coordinates snap to the 20 px grid.</summary>
/// <param name="Name">Bucket name.</param>
/// <param name="Color">Outline color; defaults to Blue.</param>
/// <param name="X">Left edge; omit X and Y to place the bucket to the right of the existing ones.</param>
/// <param name="Y">Top edge; give it together with X.</param>
/// <param name="Width">Width in pixels; defaults to 420.</param>
/// <param name="Height">Height in pixels; defaults to 600.</param>
public record CreateBucketRequest(
    [property: Required, StringLength(100)] string Name,
    BucketColor? Color = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int? Width = null,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int? Height = null);

/// <summary>
/// Renames, recolors, moves and/or resizes a bucket. Omitted (null) fields stay unchanged.
/// Moving a bucket carries its cards along, and the images on the board whose centre lies in it (the topmost bucket
/// there, as for cards). Giving a new X or Y together with a new Width or Height resizes it from a top or left corner or
/// edge instead, and its cards and images stay where they are. Afterwards every card belongs to whichever bucket now
/// contains its centre.
/// </summary>
/// <param name="Name">New name.</param>
/// <param name="Color">New outline color.</param>
/// <param name="X">New left edge.</param>
/// <param name="Y">New top edge.</param>
/// <param name="Width">New width.</param>
/// <param name="Height">New height.</param>
/// <param name="Collapsed">True folds the bucket up into a square in its top-right corner, hiding its cards; false opens it again with its cards where they were. A collapsed bucket can be moved but not resized.</param>
/// <param name="CarryCardIds">
/// Only with a move (X and/or Y without Width or Height): exactly these cards move along, instead of the cards in the
/// bucket. Used to undo a move, where the cards that came along must go back. Ids not on the board are ignored.
/// </param>
/// <param name="CarryImageIds">
/// Like carryCardIds, for images on the board: exactly these move along, instead of the images in the bucket.
/// </param>
/// <param name="CarryDrawingIds">
/// Like carryCardIds, for drawings on the board: exactly these move along, instead of the drawings in the bucket.
/// </param>
public record UpdateBucketRequest(
    [property: StringLength(100, MinimumLength = 1)] string? Name = null,
    BucketColor? Color = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int? Width = null,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int? Height = null,
    [property: MaxLength(2000)] List<Guid>? CarryCardIds = null,
    [property: MaxLength(2000)] List<Guid>? CarryImageIds = null,
    [property: MaxLength(2000)] List<Guid>? CarryDrawingIds = null,
    bool? Collapsed = null);
