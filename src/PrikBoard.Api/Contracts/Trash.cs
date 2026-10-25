using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// The board's trash: the cards deleted in the last 7 days (with deletedAt set), most recently deleted first, and the
/// images deleted along with them. restoreCard puts one back; after 7 days they are gone for good.
/// </summary>
/// <param name="Cards">The deleted cards (matching the search, if one was given), most recently deleted first. Each keeps the place it had on the board (x, y), where restoreCard puts it back.</param>
/// <param name="Images">Images deleted along with those cards. Download them from contentUrl.</param>
public record TrashContents(IReadOnlyList<CardDetail> Cards, IReadOnlyList<ImageDetail> Images);

/// <summary>What emptyTrash did.</summary>
/// <param name="Purged">How many cards were deleted for good (their images and drawings went with them).</param>
public record EmptyTrashResult(int Purged);

/// <summary>
/// Where to put a deleted card back. Without X and Y it goes back where it was; with both it goes there (a card dragged
/// out of the trash onto the board).
/// </summary>
/// <param name="X">Left edge in canvas pixels; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge in canvas pixels; snapped to the 20 px grid. Give it together with X.</param>
/// <param name="BucketId">With X and Y: the bucket it joins, which it must overlap (without one it joins the topmost one containing its center).</param>
/// <param name="Loose">With X and Y: true to join no bucket, even over one. Do not combine with bucketId.</param>
public record RestoreCardRequest(
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    Guid? BucketId = null,
    bool? Loose = null);

/// <summary>Cards, buckets, images and drawings on one board, by id: what deleteItems and restoreItems act on.</summary>
/// <param name="CardIds">Cards (with the images and drawings on them).</param>
/// <param name="BucketIds">Buckets. Deleting one leaves what is in it on the board, loose.</param>
/// <param name="ImageIds">Images, on the board or on a card.</param>
/// <param name="DrawingIds">Drawings, on the board or on a card.</param>
public record ItemIds(
    [property: MaxLength(ItemIds.MaxPerKind)] List<Guid>? CardIds = null,
    [property: MaxLength(ItemIds.MaxPerKind)] List<Guid>? BucketIds = null,
    [property: MaxLength(ItemIds.MaxPerKind)] List<Guid>? ImageIds = null,
    [property: MaxLength(ItemIds.MaxPerKind)] List<Guid>? DrawingIds = null)
{
    /// <summary>A whole board's worth of each.</summary>
    public const int MaxPerKind = 5000;
}

/// <summary>What restoreItems brought back, as they are now.</summary>
/// <param name="Cards">The cards, with the images and drawings deleted along with them.</param>
/// <param name="Buckets">The buckets.</param>
/// <param name="Images">The images named in the request.</param>
/// <param name="Drawings">The drawings named in the request.</param>
public record RestoredItems(List<CardDetail> Cards, List<BucketDetail> Buckets, List<ImageDetail> Images, List<DrawingDetail> Drawings);
