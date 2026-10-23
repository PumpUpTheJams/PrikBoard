using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A bucket: a named rectangle on the board. A card, image or drawing put down with its center inside it (where it is the
/// topmost bucket) belongs to it and moves with it.
/// </summary>
/// <param name="Id">Bucket identifier.</param>
/// <param name="BoardId">Board the bucket belongs to.</param>
/// <param name="Name">Bucket name, e.g. "To do", "Doing" or "Done".</param>
/// <param name="X">Left edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Y">Top edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Width">Width in pixels, a multiple of 20 and at least 240.</param>
/// <param name="Height">Height in pixels, a multiple of 20 and at least 160.</param>
/// <param name="Collapsed">Folded up into a 120 px square in its top-right corner: its cards, images and drawings are hidden on the board (they keep their places and still belong to it) until it is opened again.</param>
/// <param name="StackedAt">Its place in the stack, the same for everyone: the later, the higher. Where buckets overlap, the topmost is drawn above the others and an item put there joins it. Creating, moving, resizing, opening or filling a bucket puts it on top, and so does bringBucketToFront.</param>
public record BucketDetail(
    Guid Id, Guid BoardId, string Name, int X, int Y, int Width, int Height, bool Collapsed, DateTimeOffset StackedAt)
{
    public static BucketDetail From(Bucket bucket) =>
        new(bucket.Id, bucket.BoardId, bucket.Name, bucket.X, bucket.Y, bucket.Width, bucket.Height, bucket.Collapsed, bucket.StackedAt);
}

/// <summary>Creates a bucket on a board, on top of the others. The cards, images and drawings whose center it covers join it. Coordinates snap to the 20 px grid.</summary>
/// <param name="Name">Bucket name.</param>
/// <param name="X">Left edge; omit X and Y to place the bucket to the right of the existing ones.</param>
/// <param name="Y">Top edge; give it together with X.</param>
/// <param name="Width">Width in pixels; defaults to 240.</param>
/// <param name="Height">Height in pixels; defaults to 600.</param>
public record CreateBucketRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int? Width = null,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int? Height = null);

/// <summary>
/// Renames, moves, resizes or folds a bucket. Omitted (null) fields stay unchanged.
/// Moving a bucket carries its own cards, images and drawings along and changes no item's bucket, even where it ends up
/// over another bucket. Giving a new X or Y together with a new Width or Height resizes it from a top or left corner or edge
/// instead: its items stay where they are, those now outside it leave it, and loose ones it now covers join it.
/// </summary>
/// <param name="Name">New name.</param>
/// <param name="X">New left edge.</param>
/// <param name="Y">New top edge.</param>
/// <param name="Width">New width.</param>
/// <param name="Height">New height.</param>
/// <param name="Collapsed">True folds the bucket up into a square in its top-right corner, hiding what is in it; false opens it again with everything where it was. A collapsed bucket can be moved but not resized.</param>
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
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int? Width = null,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int? Height = null,
    [property: MaxLength(2000)] List<Guid>? CarryCardIds = null,
    [property: MaxLength(2000)] List<Guid>? CarryImageIds = null,
    [property: MaxLength(2000)] List<Guid>? CarryDrawingIds = null,
    bool? Collapsed = null);

/// <summary>
/// Puts cards, images and drawings into a bucket, in its free room row by row (cards first, then images, then drawings,
/// each in the order given), a grid step clear of what is already there (its own items and loose ones). When they don't
/// fit, the bucket grows towards <c>grow</c> a row or column at a time, over neighboring buckets too.
/// </summary>
/// <param name="CardIds">Cards on the bucket's board.</param>
/// <param name="ImageIds">Images on the bucket's board itself (not attached to a card).</param>
/// <param name="DrawingIds">Drawings on the bucket's board itself (not on a card).</param>
/// <param name="Grow">The side the bucket grows towards when it is full: Down (default), Right, Left or Up. Left and Up move its left or top edge out; what is in it stays.</param>
public record PlaceItemsRequest(
    [property: MaxLength(PlaceItemsRequest.MaxEach)] List<Guid>? CardIds = null,
    [property: MaxLength(PlaceItemsRequest.MaxEach)] List<Guid>? ImageIds = null,
    [property: MaxLength(PlaceItemsRequest.MaxEach)] List<Guid>? DrawingIds = null,
    GrowSide? Grow = null)
{
    /// <summary>The most ids each list takes.</summary>
    public const int MaxEach = 2000;
}

/// <summary>The bucket (possibly grown) and the items put into it.</summary>
/// <param name="Bucket">The bucket after the items went in.</param>
/// <param name="Cards">The cards in their new places, in the order given.</param>
/// <param name="Images">The images in their new places, in the order given.</param>
/// <param name="Drawings">The drawings in their new places, in the order given.</param>
public record PlacedItems(BucketDetail Bucket, List<CardDetail> Cards, List<ImageDetail> Images, List<DrawingDetail> Drawings);
