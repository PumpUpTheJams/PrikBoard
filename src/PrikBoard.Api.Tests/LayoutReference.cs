using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// <see cref="Layout.PackIn"/> and <see cref="Layout.MakeRoomIn"/> as they were before their search was made faster:
/// every grid step tried against every rectangle in the way. <see cref="LayoutSearchTests"/> checks that the fast
/// search finds the same places. Keep the grow rules (<see cref="GrowFor"/>, <see cref="FreeSpotsIn"/>, <see cref="Grow"/>)
/// the same as Layout's.
/// </summary>
internal static class LayoutReference
{
    private const int Gap = 20;
    private const int Grid = Layout.Grid;
    private const int CardWidth = Layout.CardWidth;
    private const int CardHeight = Layout.CardHeight;
    private const int ColumnStep = CardWidth + Gap;
    private const int RowStep = CardHeight + Gap;

    public static (int X, int Y) MakeRoomIn(Bucket bucket, IReadOnlyCollection<Card> cards, IReadOnlyCollection<Bucket> buckets)
    {
        var folded = buckets.Where(b => b.Collapsed && b.Id != bucket.Id).Select(b => b.Id).ToHashSet();
        var shown = cards.Where(n => n.BucketId is not { } id || !folded.Contains(id));
        return FreeSpotsIn(bucket, 1, [.. shown.Select(n => (n.X, n.Y, CardWidth, CardHeight))], buckets, GrowSide.Down)[0];
    }

    private static List<(int X, int Y)> FreeSpotsIn(
        Bucket bucket, int count, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, IEnumerable<Bucket> buckets, GrowSide grow)
    {
        var above = Layout.BackToFront(buckets).SkipWhile(b => b.Id != bucket.Id).Skip(1)
            .Select(b => b.Collapsed
                ? (X: b.X + b.Width - Layout.CollapsedBucketSize, b.Y, Width: Layout.CollapsedBucketSize, Height: Layout.CollapsedBucketSize)
                : (b.X, b.Y, b.Width, b.Height))
            .ToList();
        var spots = SlotsIn(bucket, count, taken, above);
        while (spots.Count < count && GrowAny(bucket, grow))
        {
            spots = SlotsIn(bucket, count, taken, above);
        }
        while (spots.Count < count) spots.Add((bucket.X + Gap, bucket.Y + Gap));
        return spots;
    }

    public static List<(int X, int Y)> PackIn(
        Bucket bucket, IReadOnlyList<(int Width, int Height)> sizes, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, GrowSide grow)
    {
        var spots = new (int X, int Y)?[sizes.Count];
        Pack(bucket, sizes, taken, spots);
        while (FirstUnplaced(spots) is { } next && GrowFor(bucket, sizes[next], grow)) Pack(bucket, sizes, taken, spots);
        return [.. spots.Select(spot => spot ?? (bucket.X + Gap, bucket.Y + Gap))];
    }

    private static int? FirstUnplaced((int X, int Y)?[] spots)
    {
        var index = Array.FindIndex(spots, spot => spot is null);
        return index < 0 ? null : index;
    }

    private static void Pack(
        Bucket bucket, IReadOnlyList<(int Width, int Height)> sizes, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken,
        (int X, int Y)?[] spots)
    {
        var inTheWay = Touching(taken, Bounds(bucket));
        for (var i = 0; i < spots.Length; i++)
        {
            if (spots[i] is { } spot) inTheWay.Add((spot.X, spot.Y, sizes[i].Width, sizes[i].Height));
        }
        (int X, int Y, int Width, int Height)? last = null;
        for (var i = 0; i < spots.Length; i++)
        {
            if (spots[i] is not null) continue;
            var from = last is { } l && (l.Width, l.Height) == sizes[i] ? (l.X, l.Y) : (bucket.X + Gap, bucket.Y + Gap);
            spots[i] = FirstFree(bucket, sizes[i], from, inTheWay);
            if (spots[i] is not { } spot) continue;
            last = (spot.X, spot.Y, sizes[i].Width, sizes[i].Height);
            inTheWay.Add(last.Value);
        }
    }

    private static (int X, int Y)? FirstFree(
        Bucket bucket, (int Width, int Height) size, (int X, int Y) from, List<(int X, int Y, int Width, int Height)> inTheWay)
    {
        var left = bucket.X + Gap;
        var right = bucket.X + Math.Max(bucket.Width, Layout.MinBucketWidth) - Gap;
        var bottom = bucket.Y + Math.Max(bucket.Height, Layout.MinBucketHeight) - Gap;
        for (var y = from.Y; y + size.Height <= bottom; y += Grid)
        {
            for (var x = y == from.Y ? from.X : left; x + size.Width <= right; x += Grid)
            {
                var around = (x - Gap, y - Gap, size.Width + 2 * Gap, size.Height + 2 * Gap);
                if (!AnyIntersects(inTheWay, around)) return (x, y);
            }
        }
        return null;
    }

    private static bool GrowFor(Bucket bucket, (int Width, int Height) size, GrowSide grow)
    {
        var tooWide = size.Width > Math.Max(bucket.Width, Layout.MinBucketWidth) - 2 * Gap;
        var tooTall = size.Height > Math.Max(bucket.Height, Layout.MinBucketHeight) - 2 * Gap;
        var vertical = grow is GrowSide.Down or GrowSide.Up;
        if (vertical && tooWide && (Grow(bucket, GrowSide.Right) || Grow(bucket, GrowSide.Left))) return true;
        if (!vertical && tooTall && (Grow(bucket, GrowSide.Down) || Grow(bucket, GrowSide.Up))) return true;
        return GrowAny(bucket, grow);
    }

    private static List<(int X, int Y)> SlotsIn(
        Bucket bucket, int count, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, List<(int X, int Y, int Width, int Height)> above)
    {
        var spots = new List<(int X, int Y)>();
        var right = bucket.X + Math.Max(bucket.Width, Layout.MinBucketWidth) - Gap;
        var bottom = bucket.Y + Math.Max(bucket.Height, Layout.MinBucketHeight) - Gap;
        var bounds = Bounds(bucket);
        var inTheWay = Touching(taken, bounds);
        inTheWay.AddRange(Touching(above, bounds));
        for (var y = bucket.Y + Gap; y + CardHeight <= bottom && spots.Count < count; y += RowStep)
        {
            for (var x = bucket.X + Gap; x + CardWidth <= right && spots.Count < count; x += ColumnStep)
            {
                if (AnyIntersects(inTheWay, (x, y, CardWidth, CardHeight))) continue;
                spots.Add((x, y));
            }
        }
        return spots;
    }

    /// <summary>
    /// Grows the bucket a step towards <paramref name="side"/>, else downwards, else to the right or left once it is as tall
    /// as it can be; false when it can grow no further.
    /// </summary>
    private static bool GrowAny(Bucket bucket, GrowSide side) =>
        Grow(bucket, side) || Grow(bucket, GrowSide.Down) || Grow(bucket, GrowSide.Right) || Grow(bucket, GrowSide.Left);

    private static bool Grow(Bucket bucket, GrowSide side)
    {
        var columns = (Math.Max(bucket.Width, Layout.MinBucketWidth) - Gap) / ColumnStep;
        var rows = (Math.Max(bucket.Height, Layout.MinBucketHeight) - Gap) / RowStep;
        var width = Gap + (columns + 1) * ColumnStep;
        var height = Gap + (rows + 1) * RowStep;
        switch (side)
        {
            case GrowSide.Right when width <= Layout.MaxBucketSize && bucket.X + width <= Layout.MaxCoordinate:
                bucket.Width = Math.Max(bucket.Width, width);
                return true;
            case GrowSide.Down when height <= Layout.MaxBucketSize && bucket.Y + height <= Layout.MaxCoordinate:
                bucket.Height = Math.Max(bucket.Height, height);
                return true;
            case GrowSide.Left when bucket.Width + ColumnStep <= Layout.MaxBucketSize && bucket.X - ColumnStep >= Layout.MinCoordinate:
                bucket.X -= ColumnStep;
                bucket.Width += ColumnStep;
                return true;
            case GrowSide.Up when bucket.Height + RowStep <= Layout.MaxBucketSize && bucket.Y - RowStep >= Layout.MinCoordinate:
                bucket.Y -= RowStep;
                bucket.Height += RowStep;
                return true;
            default:
                return false;
        }
    }

    private static (int X, int Y, int Width, int Height) Bounds(Bucket bucket) =>
        (bucket.X, bucket.Y, Math.Max(bucket.Width, Layout.MinBucketWidth), Math.Max(bucket.Height, Layout.MinBucketHeight));

    private static List<(int X, int Y, int Width, int Height)> Touching(
        IEnumerable<(int X, int Y, int Width, int Height)> rects, (int X, int Y, int Width, int Height) area) =>
        [.. rects.Where(rect => Layout.Intersects(rect, area))];

    private static bool AnyIntersects(List<(int X, int Y, int Width, int Height)> rects, (int X, int Y, int Width, int Height) rect) =>
        rects.Exists(other => Layout.Intersects(other, rect));
}
