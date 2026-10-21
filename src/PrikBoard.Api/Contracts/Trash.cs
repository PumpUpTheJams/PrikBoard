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
public record RestoreCardRequest(
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null);
