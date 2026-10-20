using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// The board's trash: the notes deleted in the last 7 days (with deletedAt set), most recently deleted first, and the
/// images deleted along with them. restoreNote puts one back; after 7 days they are gone for good.
/// </summary>
/// <param name="Notes">The deleted notes (matching the search, if one was given), most recently deleted first. Each keeps the place it had on the board (x, y), where restoreNote puts it back.</param>
/// <param name="Images">Images deleted along with those notes. Download them from contentUrl.</param>
public record TrashContents(IReadOnlyList<NoteDetail> Notes, IReadOnlyList<ImageDetail> Images);

/// <summary>
/// Where to put a deleted note back. Without X and Y it goes back where it was; with both it goes there (a note dragged
/// out of the trash onto the board).
/// </summary>
/// <param name="X">Left edge in canvas pixels; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge in canvas pixels; snapped to the 20 px grid. Give it together with X.</param>
public record RestoreNoteRequest(
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null);
