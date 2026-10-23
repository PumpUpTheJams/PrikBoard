namespace PrikBoard.Api.Domain;

/// <summary>
/// The geometry rules of a board. Coordinates are canvas pixels, snapped to a grid. The board has no edge: items can go
/// left of and above the origin (negative coordinates) as well as right of and below it.
/// A card, image or drawing that moves joins the topmost bucket containing its center, and stays in it while buckets move
/// (see the membership rules below). Buckets stack by <see cref="Bucket.StackedAt"/>: the one brought to the front last is on top.
/// </summary>
public static class Layout
{
    public const int Grid = 20;
    public const int CardWidth = 200;
    public const int CardHeight = 120;
    /// <summary>Room for one column of cards with a gap on either side.</summary>
    public const int MinBucketWidth = CardWidth + 2 * Gap;
    public const int MinBucketHeight = CardHeight + 2 * Gap;
    public const int DefaultBucketWidth = MinBucketWidth;
    public const int DefaultBucketHeight = 600;
    public const int MinCoordinate = -MaxCoordinate;
    public const int MaxCoordinate = 100_000;
    public const int MaxBucketSize = 10_000;
    public const int MinImageWidth = 80;
    public const int MaxImageWidth = 4000;
    /// <summary>Width a new image gets when none is given (smaller images keep their own width).</summary>
    public const int DefaultImageWidth = 360;
    /// <summary>A collapsed bucket is a square this size in its top-right corner.</summary>
    public const int CollapsedBucketSize = CardHeight;

    /// <summary>A card's own canvas (its components and attached images) runs from 0 to this, in both directions.</summary>
    public const int CardCanvasMax = 10_000;
    public const int MinComponentWidth = 160;
    public const int MinComponentHeight = 80;
    public const int MaxComponentSize = 2000;

    /// <summary>Gap between neighboring cards and buckets, and the inset of cards from a bucket's edge.</summary>
    private const int Gap = 20;
    /// <summary>How far right components are laid out on a card's canvas before starting a new row.</summary>
    private const int ComponentAreaWidth = 1100;
    /// <summary>How far right loose cards are laid out before starting a new row.</summary>
    private const int LooseAreaWidth = 1200;

    public static int Snap(int value) => (int)Math.Round(value / (double)Grid, MidpointRounding.AwayFromZero) * Grid;

    public static int SnapSize(int value, int min) => Math.Max(min, Snap(value));

    /// <summary>Buckets in stacking order, back to front (<see cref="Bucket.StackedAt"/>).</summary>
    public static List<Bucket> BackToFront(IEnumerable<Bucket> buckets) =>
        buckets.OrderBy(b => b.StackedAt).ThenBy(b => b.CreatedAt).ThenBy(b => b.Id).ToList();

    /// <summary>Buckets in the order lists show them, oldest first (To do, Doing, Done), however they are stacked.</summary>
    public static List<Bucket> InListOrder(IEnumerable<Bucket> buckets) =>
        buckets.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).ToList();

    // Bucket membership. A card, image or drawing on the board keeps its bucket (BucketId) until it moves itself: then it
    // joins the topmost bucket containing its center (Place). Changes to a bucket only touch its own items, so moving a
    // bucket onto another never moves items from one to the other: a moved bucket carries its own items and takes none;
    // a resized one lets go of its items now outside it (Release) and takes the loose ones it now covers (Adopt,
    // looseOnly); a created or restored one takes everything it covers (Adopt); a deleted one lets all its items go.
    // A collapsed bucket keeps its items but takes no others, since they would vanish into it. The exception is a card
    // put into it by bucketId (dropped on its square).

    /// <summary>For a card that moved: puts it in the topmost open bucket containing its center, or makes it loose.</summary>
    public static void Place(Card card, IReadOnlyList<Bucket> stacked) => card.BucketId = TopmostAt(stacked, Centre(card))?.Id;

    /// <summary>
    /// Puts a card in <paramref name="bucket"/> if the bucket contains its center, even when another bucket is stacked above
    /// it there (the web app may show this one in front) or it is collapsed (the card was dropped on its square). Returns
    /// false and changes nothing otherwise.
    /// </summary>
    public static bool PlaceIn(Card card, Bucket bucket)
    {
        var (x, y) = Centre(card);
        if (!bucket.Contains(x, y)) return false;
        card.BucketId = bucket.Id;
        return true;
    }

    /// <summary>Likewise for an image or drawing; one on a card is in no bucket.</summary>
    public static void Place(ICanvasItem item, IReadOnlyList<Bucket> stacked) =>
        item.BucketId = item.CardId is null ? TopmostAt(stacked, Centre(item))?.Id : null;

    /// <summary>Places every card, image and drawing by position, for a board built from scratch (an import).</summary>
    public static void PlaceAll(IEnumerable<Card> cards, IEnumerable<ICanvasItem> items, IEnumerable<Bucket> buckets)
    {
        var stacked = BackToFront(buckets);
        foreach (var card in cards) Place(card, stacked);
        foreach (var item in items) Place(item, stacked);
    }

    /// <summary>
    /// Keeps a card in its bucket if that bucket is still in <paramref name="stacked"/> and contains its center;
    /// otherwise places it by position. For a restored card, or one its bucket let go.
    /// </summary>
    public static void Recheck(Card card, IReadOnlyList<Bucket> stacked)
    {
        if (!StillIn(card.BucketId, Centre(card), stacked)) Place(card, stacked);
    }

    /// <summary>Likewise for an image or drawing.</summary>
    public static void Recheck(ICanvasItem item, IReadOnlyList<Bucket> stacked)
    {
        if (item.CardId is not null || !StillIn(item.BucketId, Centre(item), stacked)) Place(item, stacked);
    }

    /// <summary>
    /// Items whose center lies in <paramref name="bucket"/>, where it is the topmost bucket, join it: all of them (a bucket
    /// created or restored around them) or, with <paramref name="looseOnly"/>, only loose ones (a bucket that grew).
    /// A collapsed bucket takes none.
    /// </summary>
    public static void Adopt(
        Bucket bucket, IReadOnlyList<Bucket> stacked, IEnumerable<Card> cards, IEnumerable<ICanvasItem> items, bool looseOnly)
    {
        if (bucket.Collapsed) return;
        foreach (var card in cards)
        {
            if ((!looseOnly || card.BucketId is null) && TopmostAt(stacked, Centre(card)) == bucket) card.BucketId = bucket.Id;
        }
        foreach (var item in items)
        {
            if (item.CardId is null && (!looseOnly || item.BucketId is null) && TopmostAt(stacked, Centre(item)) == bucket)
            {
                item.BucketId = bucket.Id;
            }
        }
    }

    /// <summary>
    /// Items of <paramref name="bucket"/> that are no longer inside it go to whatever is under them (another bucket, or
    /// loose). For a deleted bucket, leave it out of <paramref name="stacked"/> so all its items go.
    /// </summary>
    public static void Release(Bucket bucket, IReadOnlyList<Bucket> stacked, IEnumerable<Card> cards, IEnumerable<ICanvasItem> items)
    {
        foreach (var card in cards.Where(n => n.BucketId == bucket.Id)) Recheck(card, stacked);
        foreach (var item in items.Where(i => i.BucketId == bucket.Id)) Recheck(item, stacked);
    }

    private static (int X, int Y) Centre(Card card) => (card.X + CardWidth / 2, card.Y + CardHeight / 2);

    private static (int X, int Y) Centre(ICanvasItem item) => (item.X + item.Width / 2, item.Y + item.Height / 2);

    private static bool StillIn(Guid? bucketId, (int X, int Y) centre, IReadOnlyList<Bucket> stacked) =>
        bucketId is { } id && stacked.Any(b => b.Id == id && b.Contains(centre.X, centre.Y));

    /// <summary>The topmost of <paramref name="stacked"/> (back to front) containing a point, leaving out collapsed ones.</summary>
    private static Bucket? TopmostAt(IReadOnlyList<Bucket> stacked, (int X, int Y) point) =>
        stacked.LastOrDefault(b => !b.Collapsed && b.Contains(point.X, point.Y));

    /// <summary>Cards bucket by bucket (in list order, loose cards last), then top to bottom and left to right.</summary>
    public static List<Card> ReadingOrder(IEnumerable<Card> cards, IEnumerable<Bucket> buckets)
    {
        var rank = InListOrder(buckets).Index().ToDictionary(p => p.Item.Id, p => p.Index);
        return cards
            .OrderBy(n => n.BucketId is { } id && rank.TryGetValue(id, out var r) ? r : int.MaxValue)
            .ThenBy(n => n.Y).ThenBy(n => n.X)
            .ToList();
    }

    /// <summary>
    /// The first free card slot inside <paramref name="bucket"/>, row by row, clear of other cards. When the bucket is
    /// full it grows downwards a row at a time to make room. Slots under a bucket stacked above it are skipped (the card
    /// would belong to that one), so a bucket that grows into another one continues below it. Cards folded away in
    /// another collapsed bucket take no room.
    /// </summary>
    public static (int X, int Y) MakeRoomIn(Bucket bucket, IReadOnlyCollection<Card> cards, IReadOnlyCollection<Bucket> buckets)
    {
        var folded = buckets.Where(b => b.Collapsed && b.Id != bucket.Id).Select(b => b.Id).ToHashSet();
        var shown = cards.Where(n => n.BucketId is not { } id || !folded.Contains(id));
        return FreeSpotsIn(bucket, 1, [.. shown.Select(n => (n.X, n.Y, CardWidth, CardHeight))], buckets, GrowSide.Down)[0];
    }

    /// <summary>
    /// The first <paramref name="count"/> slots of a card-sized grid inside <paramref name="bucket"/>, row by row, clear
    /// of what is <paramref name="taken"/> and of buckets stacked above it (a card placed there by position would belong to
    /// that one; for a collapsed one only its square counts, as the rest takes no cards). When there aren't enough, the
    /// bucket grows a row or column at a time towards <paramref name="grow"/> (growing left or up moves that edge out;
    /// what is in it stays put), and downwards once that side reaches the size or coordinate limit. Slots that can't be
    /// found at all fall back to the top-left one.
    /// </summary>
    private static List<(int X, int Y)> FreeSpotsIn(
        Bucket bucket, int count, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, IEnumerable<Bucket> buckets, GrowSide grow)
    {
        var above = BackToFront(buckets).SkipWhile(b => b.Id != bucket.Id).Skip(1)
            .Select(b => b.Collapsed
                ? (X: b.X + b.Width - CollapsedBucketSize, b.Y, Width: CollapsedBucketSize, Height: CollapsedBucketSize)
                : (b.X, b.Y, b.Width, b.Height))
            .ToList();
        var spots = SlotsIn(bucket, count, taken, above);
        while (spots.Count < count && (Grow(bucket, grow) || Grow(bucket, GrowSide.Down)))
        {
            spots = SlotsIn(bucket, count, taken, above);
        }
        while (spots.Count < count) spots.Add((bucket.X + Gap, bucket.Y + Gap));
        return spots;
    }

    /// <summary>
    /// Where items put into a bucket by id go (placeItemsInBucket), one for each of <paramref name="sizes"/> in turn: the
    /// first place on the 20 px grid inside it, row by row, a gap clear of what is <paramref name="taken"/> (the bucket's
    /// own items and loose ones) and of the items placed before it, so any room big enough for the item is used. The items
    /// join the bucket by id wherever they sit, so other buckets' items don't count. When they don't fit, the bucket grows a
    /// row or column of card slots at a time towards <paramref name="grow"/> (or the way an item too wide or too tall
    /// needs), over its neighbors too, as in <see cref="FreeSpotsIn"/>. An item that can't be placed at all goes to the
    /// top-left corner.
    /// </summary>
    public static List<(int X, int Y)> PackIn(
        Bucket bucket, IReadOnlyList<(int Width, int Height)> sizes, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, GrowSide grow)
    {
        // Fill the room it has first; only the rest goes into the room it grows by.
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

    /// <summary>Gives each item without a place in <paramref name="spots"/> the first free one inside the bucket as it is now.</summary>
    private static void Pack(
        Bucket bucket, IReadOnlyList<(int Width, int Height)> sizes, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken,
        (int X, int Y)?[] spots)
    {
        // Every place with the gap around it lies inside the bucket, so only what reaches into the bucket can be in the way.
        var inTheWay = Touching(taken, Bounds(bucket));
        // Items placed before the bucket grew keep their places.
        for (var i = 0; i < spots.Length; i++)
        {
            if (spots[i] is { } spot) inTheWay.Add((spot.X, spot.Y, sizes[i].Width, sizes[i].Height));
        }
        (int X, int Y, int Width, int Height)? last = null;
        for (var i = 0; i < spots.Length; i++)
        {
            if (spots[i] is not null) continue;
            // None of the places before the last one this pass found suit an item of its size, so the search goes on from there.
            var from = last is { } l && (l.Width, l.Height) == sizes[i] ? (l.X, l.Y) : (bucket.X + Gap, bucket.Y + Gap);
            spots[i] = FirstFree(bucket, sizes[i], from, inTheWay);
            if (spots[i] is not { } spot) continue;
            last = (spot.X, spot.Y, sizes[i].Width, sizes[i].Height);
            inTheWay.Add(last.Value);
        }
    }

    /// <summary>The first place for an item of <paramref name="size"/> inside the bucket from <paramref name="from"/> on, row by row, a gap clear of <paramref name="inTheWay"/>.</summary>
    private static (int X, int Y)? FirstFree(
        Bucket bucket, (int Width, int Height) size, (int X, int Y) from, List<(int X, int Y, int Width, int Height)> inTheWay)
    {
        var left = bucket.X + Gap;
        var right = bucket.X + Math.Max(bucket.Width, MinBucketWidth) - Gap;
        var bottom = bucket.Y + Math.Max(bucket.Height, MinBucketHeight) - Gap;
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

    /// <summary>
    /// Grows the bucket for an item that found no place: towards <paramref name="grow"/>, except across when the item is
    /// wider or taller than the bucket's room that way, then downwards once that side reaches the limit.
    /// </summary>
    private static bool GrowFor(Bucket bucket, (int Width, int Height) size, GrowSide grow)
    {
        var tooWide = size.Width > Math.Max(bucket.Width, MinBucketWidth) - 2 * Gap;
        var tooTall = size.Height > Math.Max(bucket.Height, MinBucketHeight) - 2 * Gap;
        var vertical = grow is GrowSide.Down or GrowSide.Up;
        if (vertical && tooWide && (Grow(bucket, GrowSide.Right) || Grow(bucket, GrowSide.Left))) return true;
        if (!vertical && tooTall && (Grow(bucket, GrowSide.Down) || Grow(bucket, GrowSide.Up))) return true;
        return Grow(bucket, grow) || Grow(bucket, GrowSide.Down);
    }

    private const int ColumnStep = CardWidth + Gap;
    private const int RowStep = CardHeight + Gap;

    /// <summary>The free card slots inside a bucket as it is now, row by row, at most <paramref name="count"/>.</summary>
    private static List<(int X, int Y)> SlotsIn(
        Bucket bucket, int count, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, List<(int X, int Y, int Width, int Height)> above)
    {
        var spots = new List<(int X, int Y)>();
        var right = bucket.X + Math.Max(bucket.Width, MinBucketWidth) - Gap;
        var bottom = bucket.Y + Math.Max(bucket.Height, MinBucketHeight) - Gap;
        // Slots lie inside the bucket, so only what reaches into it can cover one.
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
    /// Makes a bucket one row or column of card slots bigger towards a side (slots are laid out from its top-left).
    /// Returns false when that would pass the size or coordinate limit.
    /// </summary>
    private static bool Grow(Bucket bucket, GrowSide side)
    {
        var columns = (Math.Max(bucket.Width, MinBucketWidth) - Gap) / ColumnStep;
        var rows = (Math.Max(bucket.Height, MinBucketHeight) - Gap) / RowStep;
        var width = Gap + (columns + 1) * ColumnStep;
        var height = Gap + (rows + 1) * RowStep;
        switch (side)
        {
            case GrowSide.Right when width <= MaxBucketSize && bucket.X + width <= MaxCoordinate:
                bucket.Width = Math.Max(bucket.Width, width);
                return true;
            case GrowSide.Down when height <= MaxBucketSize && bucket.Y + height <= MaxCoordinate:
                bucket.Height = Math.Max(bucket.Height, height);
                return true;
            // Out to the left or top by a whole step, so the slots inside line up with the cards already there.
            case GrowSide.Left when bucket.Width + ColumnStep <= MaxBucketSize && bucket.X - ColumnStep >= MinCoordinate:
                bucket.X -= ColumnStep;
                bucket.Width += ColumnStep;
                return true;
            case GrowSide.Up when bucket.Height + RowStep <= MaxBucketSize && bucket.Y - RowStep >= MinCoordinate:
                bucket.Y -= RowStep;
                bucket.Height += RowStep;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The first grid slot near the top-left of the board that is clear of every bucket and card.</summary>
    public static (int X, int Y) FreeSpotOutside(IReadOnlyCollection<Bucket> buckets, IReadOnlyCollection<Card> cards)
    {
        List<(int X, int Y, int Width, int Height)> taken =
            [.. buckets.Select(b => (b.X, b.Y, b.Width, b.Height)), .. cards.Select(n => (n.X, n.Y, CardWidth, CardHeight))];
        for (var y = Gap * 2; y < MaxCoordinate; y += CardHeight + Gap)
        {
            for (var x = Gap * 2; x < LooseAreaWidth; x += CardWidth + Gap)
            {
                if (!AnyIntersects(taken, (x, y, CardWidth, CardHeight))) return (x, y);
            }
        }
        return (Gap * 2, Gap * 2);
    }

    /// <summary>Where a new image goes when no position is given: to the right of everything on the board.</summary>
    public static (int X, int Y) NextImageSpot(
        IReadOnlyCollection<Bucket> buckets, IReadOnlyCollection<Card> cards, IReadOnlyCollection<BoardImage> images)
    {
        var right = buckets.Select(b => b.X + b.Width)
            .Concat(cards.Select(n => n.X + CardWidth))
            .Concat(images.Where(i => i.CardId is null).Select(i => i.X + i.Width))
            .DefaultIfEmpty(0)
            .Max();
        return (Snap(right + Gap * 2), Gap * 2);
    }

    /// <summary>Where a new bucket goes when no position is given: to the right of the existing ones.</summary>
    public static (int X, int Y) NextBucketSpot(IReadOnlyCollection<Bucket> buckets) =>
        (buckets.Count == 0 ? Gap * 2 : buckets.Max(b => b.X + b.Width) + Gap * 2, Gap * 2);

    /// <summary>The size a new component gets when none is given.</summary>
    public static (int Width, int Height) DefaultComponentSize(CardComponentKind kind) => kind switch
    {
        CardComponentKind.Checklist => (340, 260),
        CardComponentKind.Links => (360, 220),
        _ => (340, 220),
    };

    /// <summary>Snaps a component to the grid and keeps it within the limits and on the card's canvas.</summary>
    public static void Clamp(CardComponent component)
    {
        component.Width = Math.Min(MaxComponentSize, SnapSize(component.Width, MinComponentWidth));
        component.Height = Math.Min(MaxComponentSize, SnapSize(component.Height, MinComponentHeight));
        component.X = Math.Clamp(Snap(component.X), 0, CardCanvasMax - component.Width);
        component.Y = Math.Clamp(Snap(component.Y), 0, CardCanvasMax - component.Height);
    }

    /// <summary>A position on a card's canvas, snapped and kept on it.</summary>
    public static (int X, int Y) OnCardCanvas(int x, int y, int width, int height) =>
        (Math.Clamp(Snap(x), 0, Math.Max(0, CardCanvasMax - width)), Math.Clamp(Snap(y), 0, Math.Max(0, CardCanvasMax - height)));

    /// <summary>
    /// The first grid slot on a card's canvas, row by row, where a <paramref name="width"/> by <paramref name="height"/>
    /// rectangle keeps a gap from everything already there (its components and attached images).
    /// </summary>
    public static (int X, int Y) NextCardSpot(IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, int width, int height)
    {
        List<(int X, int Y, int Width, int Height)> rects = [.. taken];
        for (var y = Gap; y + height <= CardCanvasMax; y += Grid)
        {
            for (var x = Gap; x == Gap || x + width <= ComponentAreaWidth; x += Grid)
            {
                if (!AnyIntersects(rects, (x - Gap, y - Gap, width + 2 * Gap, height + 2 * Gap))) return (x, y);
            }
        }
        return (Gap, Gap);
    }

    /// <summary>What is on a card's canvas: its components, and the images attached to it.</summary>
    public static List<(int X, int Y, int Width, int Height)> TakenOnCard(Card card, IEnumerable<BoardImage> attached) =>
        [.. card.Components.Select(b => (b.X, b.Y, b.Width, b.Height)), .. attached.Select(i => (i.X, i.Y, i.Width, i.Height))];

    public static bool Intersects((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>The rectangle a bucket's cards are laid out in: at least the smallest bucket's size.</summary>
    private static (int X, int Y, int Width, int Height) Bounds(Bucket bucket) =>
        (bucket.X, bucket.Y, Math.Max(bucket.Width, MinBucketWidth), Math.Max(bucket.Height, MinBucketHeight));

    /// <summary>The rectangles that reach into <paramref name="area"/>.</summary>
    private static List<(int X, int Y, int Width, int Height)> Touching(
        IEnumerable<(int X, int Y, int Width, int Height)> rects, (int X, int Y, int Width, int Height) area)
    {
        var touching = new List<(int X, int Y, int Width, int Height)>();
        foreach (var rect in rects)
        {
            if (Intersects(rect, area)) touching.Add(rect);
        }
        return touching;
    }

    private static bool AnyIntersects(List<(int X, int Y, int Width, int Height)> rects, (int X, int Y, int Width, int Height) rect)
    {
        foreach (var other in rects)
        {
            if (Intersects(other, rect)) return true;
        }
        return false;
    }
}
