namespace PrikBoard.Api.Domain;

/// <summary>
/// The geometry rules of a board. Coordinates are canvas pixels, snapped to a grid. The board has no edge: things can go
/// left of and above the origin (negative coordinates) as well as right of and below it.
/// A card, image or drawing put somewhere joins the topmost bucket containing its centre, and stays in it while buckets move (see
/// the membership rules below); buckets stack by <see cref="Bucket.StackedAt"/>, the one put on top last on top.
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

    /// <summary>Gap between neighbouring cards and buckets, and the inset of cards from a bucket's edge.</summary>
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

    // Bucket membership. A card, image or drawing on the board keeps the bucket it is in (BucketId) until it moves itself: then
    // it joins the topmost bucket containing its centre (Place). Changes to buckets only touch the items they concern, so
    // moving a bucket onto another never moves items from one to the other: a moved bucket carries exactly its own items
    // and takes nothing; a resized one lets go of its items now outside it (Release) and takes loose ones it now covers
    // (Adopt, looseOnly); a drawn or restored one takes what it covers (Adopt); a deleted one lets all its items go.
    // A collapsed bucket keeps its items but takes no others: they would vanish into it (an item put into it by bucketId,
    // dropped on its square, is the exception).

    /// <summary>Puts a card in the topmost bucket containing its centre (none: loose). For a card that moved.</summary>
    public static void Place(Card card, IReadOnlyList<Bucket> stacked) => card.BucketId = TopmostAt(stacked, Centre(card))?.Id;

    /// <summary>
    /// Puts a card in <paramref name="bucket"/> when that contains its centre, even with another bucket stacked above it
    /// there (a drop on the bucket a screen shows in front) or collapsed (asked for by name). False (and nothing changes) otherwise.
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
    /// Keeps a card in its bucket while that bucket is still in <paramref name="stacked"/> and contains its centre;
    /// otherwise places it by position. For a card coming back (restored) or let go by its bucket.
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
    /// The items whose centre <paramref name="bucket"/> covers, where it is the topmost bucket, join it: all of them (a
    /// bucket drawn or restored around them) or, with <paramref name="looseOnly"/>, only loose ones (a bucket that grew).
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
    /// The items of <paramref name="bucket"/> that are no longer inside it go to whatever is under them (another bucket,
    /// or loose). Leave the bucket out of <paramref name="stacked"/> when it is deleted, and all its items go.
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
        var rank = InListOrder(buckets).Select((b, i) => (b.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return cards
            .OrderBy(n => n.BucketId is { } id && rank.TryGetValue(id, out var r) ? r : int.MaxValue)
            .ThenBy(n => n.Y).ThenBy(n => n.X)
            .ToList();
    }

    /// <summary>
    /// The first grid slot inside <paramref name="bucket"/>, row by row, not covered by another card. When the bucket is
    /// full it grows downwards by a row to make room, so a bucket always fits its cards. Slots
    /// under a bucket stacked above it are skipped (the card would belong to that one), so a bucket growing towards
    /// another one continues below it. Cards folded away in another collapsed bucket take no room.
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
    /// that one; of a collapsed one only its square, as the rest of it takes no cards). When there aren't enough, the
    /// bucket grows by a row or column at a time towards <paramref name="grow"/> (left and up move its edge out; what is
    /// in it stays put), downwards once that side reaches the size or coordinate limit. Slots that can't be found at all
    /// are the top-left one.
    /// </summary>
    public static List<(int X, int Y)> FreeSpotsIn(
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
    /// Where cards put into a bucket by id go (placeCardsInBucket): the first <paramref name="count"/> places on the 20 px
    /// grid inside it, row by row, a gap clear of what is <paramref name="taken"/> (the bucket's own cards and images, and
    /// loose ones) and of each other, so any room big enough for a card is used. They belong to the bucket wherever they
    /// sit, so other buckets' items don't count: when they don't fit, the bucket grows a row or column at a time towards
    /// <paramref name="grow"/>, over its neighbours too, as <see cref="FreeSpotsIn"/> does.
    /// </summary>
    public static List<(int X, int Y)> PackIn(Bucket bucket, int count, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, GrowSide grow)
    {
        // The room it has is used first; what it grows by only takes the rest.
        var spots = new List<(int X, int Y)>();
        Pack(bucket, count, taken, spots);
        while (spots.Count < count && (Grow(bucket, grow) || Grow(bucket, GrowSide.Down))) Pack(bucket, count, taken, spots);
        while (spots.Count < count) spots.Add((bucket.X + Gap, bucket.Y + Gap));
        return spots;
    }

    /// <summary>Adds free places for cards inside a bucket as it is now to <paramref name="spots"/>, row by row on the 20 px grid, up to <paramref name="count"/>.</summary>
    private static void Pack(Bucket bucket, int count, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, List<(int X, int Y)> spots)
    {
        var right = bucket.X + Math.Max(bucket.Width, MinBucketWidth) - Gap;
        var bottom = bucket.Y + Math.Max(bucket.Height, MinBucketHeight) - Gap;
        for (var y = bucket.Y + Gap; y + CardHeight <= bottom && spots.Count < count; y += Grid)
        {
            for (var x = bucket.X + Gap; x + CardWidth <= right && spots.Count < count; x += Grid)
            {
                var around = (x - Gap, y - Gap, CardWidth + 2 * Gap, CardHeight + 2 * Gap);
                if (taken.Any(t => Intersects(t, around)) || spots.Any(s => Intersects((s.X, s.Y, CardWidth, CardHeight), around))) continue;
                spots.Add((x, y));
            }
        }
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
        for (var y = bucket.Y + Gap; y + CardHeight <= bottom && spots.Count < count; y += RowStep)
        {
            for (var x = bucket.X + Gap; x + CardWidth <= right && spots.Count < count; x += ColumnStep)
            {
                if (taken.Any(t => Overlaps(t.X, t.Y, t.Width, t.Height, x, y))) continue;
                if (above.Any(b => Overlaps(b.X, b.Y, b.Width, b.Height, x, y))) continue;
                spots.Add((x, y));
            }
        }
        return spots;
    }

    /// <summary>
    /// Makes a bucket one row or column bigger towards a side: just big enough for one more of them, as cards are laid
    /// out from its top-left. False when that would pass the size or coordinate limit.
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
        for (var y = Gap * 2; y < MaxCoordinate; y += CardHeight + Gap)
        {
            for (var x = Gap * 2; x < LooseAreaWidth; x += CardWidth + Gap)
            {
                if (!buckets.Any(b => Overlaps(b.X, b.Y, b.Width, b.Height, x, y))
                    && !cards.Any(n => Overlaps(n.X, n.Y, CardWidth, CardHeight, x, y)))
                {
                    return (x, y);
                }
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
        for (var y = Gap; y + height <= CardCanvasMax; y += Grid)
        {
            for (var x = Gap; x == Gap || x + width <= ComponentAreaWidth; x += Grid)
            {
                if (!taken.Any(t => Intersects(t, (x - Gap, y - Gap, width + 2 * Gap, height + 2 * Gap)))) return (x, y);
            }
        }
        return (Gap, Gap);
    }

    /// <summary>What is on a card's canvas: its components, and the images attached to it.</summary>
    public static List<(int X, int Y, int Width, int Height)> TakenOnCard(Card card, IEnumerable<BoardImage> attached) =>
        [.. card.Components.Select(b => (b.X, b.Y, b.Width, b.Height)), .. attached.Select(i => (i.X, i.Y, i.Width, i.Height))];

    public static bool Intersects((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>Whether a rectangle overlaps a card-sized rectangle at (<paramref name="cardX"/>, <paramref name="cardY"/>).</summary>
    private static bool Overlaps(int x, int y, int width, int height, int cardX, int cardY) =>
        x < cardX + CardWidth && cardX < x + width && y < cardY + CardHeight && cardY < y + height;
}
