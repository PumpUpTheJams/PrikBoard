using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A bucket: a named rectangle on the board. Notes whose centre lies inside it belong to it.</summary>
/// <param name="Id">Bucket identifier.</param>
/// <param name="BoardId">Board the bucket belongs to.</param>
/// <param name="Name">Bucket name, e.g. "To do", "Doing" or "Done".</param>
/// <param name="Color">Outline colour.</param>
/// <param name="X">Left edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Y">Top edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Width">Width in pixels, a multiple of 20 and at least 200.</param>
/// <param name="Height">Height in pixels, a multiple of 20 and at least 200.</param>
public record BucketDetail(Guid Id, Guid BoardId, string Name, BucketColor Color, int X, int Y, int Width, int Height)
{
    public static BucketDetail From(Bucket bucket) =>
        new(bucket.Id, bucket.BoardId, bucket.Name, bucket.Color, bucket.X, bucket.Y, bucket.Width, bucket.Height);
}

/// <summary>Draws a bucket on a board. Loose notes it covers join it. Coordinates snap to the 20 px grid.</summary>
/// <param name="Name">Bucket name.</param>
/// <param name="Color">Outline colour; defaults to Blue.</param>
/// <param name="X">Left edge; omit X and Y to place the bucket to the right of the existing ones.</param>
/// <param name="Y">Top edge; give it together with X.</param>
/// <param name="Width">Width in pixels; defaults to 420.</param>
/// <param name="Height">Height in pixels; defaults to 600.</param>
public record CreateBucketRequest(
    [property: Required, StringLength(100)] string Name,
    BucketColor? Color = null,
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int? Width = null,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int? Height = null);

/// <summary>
/// Renames, recolours, moves and/or resizes a bucket. Omitted (null) fields stay unchanged.
/// Moving a bucket carries its notes along. Giving a new X or Y together with a new Width or Height resizes it from a
/// top or left corner or edge instead, and its notes stay where they are. Afterwards every note belongs to whichever bucket now
/// contains its centre.
/// </summary>
/// <param name="Name">New name.</param>
/// <param name="Color">New outline colour.</param>
/// <param name="X">New left edge.</param>
/// <param name="Y">New top edge.</param>
/// <param name="Width">New width.</param>
/// <param name="Height">New height.</param>
public record UpdateBucketRequest(
    [property: StringLength(100, MinimumLength = 1)] string? Name = null,
    BucketColor? Color = null,
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null,
    [property: Range(Layout.MinBucketWidth, Layout.MaxBucketSize)] int? Width = null,
    [property: Range(Layout.MinBucketHeight, Layout.MaxBucketSize)] int? Height = null);
