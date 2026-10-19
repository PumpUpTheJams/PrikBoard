namespace PrikBoard.Api.Domain;

/// <summary>
/// The geometry rules of a board. Coordinates are canvas pixels from the top-left, snapped to a grid.
/// A note belongs to the topmost bucket containing its centre; buckets stack in creation order, newest on top.
/// </summary>
public static class Layout
{
    public const int Grid = 20;
    public const int NoteWidth = 200;
    public const int NoteHeight = 120;
    /// <summary>Room for one column of notes with a gap on either side.</summary>
    public const int MinBucketWidth = NoteWidth + 2 * Gap;
    public const int MinBucketHeight = NoteHeight + 2 * Gap;
    public const int DefaultBucketWidth = MinBucketWidth;
    public const int DefaultBucketHeight = 600;
    public const int MaxCoordinate = 100_000;
    public const int MaxBucketSize = 10_000;
    public const int MinImageWidth = 80;
    public const int MaxImageWidth = 4000;
    /// <summary>Width a new image gets when none is given (smaller images keep their own width).</summary>
    public const int DefaultImageWidth = 360;

    /// <summary>A note's own canvas (its blocks and attached images) runs from 0 to this, in both directions.</summary>
    public const int NoteCanvasMax = 10_000;
    public const int MinBlockWidth = 160;
    public const int MinBlockHeight = 80;
    public const int MaxBlockSize = 2000;

    /// <summary>Gap between neighbouring notes and buckets, and the inset of notes from a bucket's edge.</summary>
    private const int Gap = 20;
    /// <summary>How far right blocks are laid out on a note's canvas before starting a new row.</summary>
    private const int BlockAreaWidth = 1100;
    /// <summary>How far right loose notes are laid out before starting a new row.</summary>
    private const int LooseAreaWidth = 1200;

    public static int Snap(int value) => (int)Math.Round(value / (double)Grid, MidpointRounding.AwayFromZero) * Grid;

    public static int SnapSize(int value, int min) => Math.Max(min, Snap(value));

    /// <summary>Buckets in stacking order, back to front.</summary>
    public static List<Bucket> BackToFront(IEnumerable<Bucket> buckets) =>
        buckets.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).ToList();

    /// <summary>Sets <see cref="Note.BucketId"/> from where each note's centre lies.</summary>
    public static void AssignBuckets(IEnumerable<Note> notes, IEnumerable<Bucket> buckets)
    {
        var stacked = BackToFront(buckets);
        foreach (var note in notes)
        {
            var centerX = note.X + NoteWidth / 2;
            var centerY = note.Y + NoteHeight / 2;
            note.BucketId = stacked.LastOrDefault(b => b.Contains(centerX, centerY))?.Id;
        }
    }

    /// <summary>Notes bucket by bucket (in stacking order, loose notes last), then top to bottom and left to right.</summary>
    public static List<Note> ReadingOrder(IEnumerable<Note> notes, IEnumerable<Bucket> buckets)
    {
        var rank = BackToFront(buckets).Select((b, i) => (b.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return notes
            .OrderBy(n => n.BucketId is { } id && rank.TryGetValue(id, out var r) ? r : int.MaxValue)
            .ThenBy(n => n.Y).ThenBy(n => n.X)
            .ToList();
    }

    /// <summary>
    /// The first grid slot inside <paramref name="bucket"/>, row by row, not covered by another note. When the bucket is
    /// full it grows downwards by a row to make room, so buckets fit their notes the way frames on a board do.
    /// </summary>
    public static (int X, int Y) MakeRoomIn(Bucket bucket, IReadOnlyCollection<Note> notes)
    {
        var left = bucket.X + Gap;
        var right = bucket.X + Math.Max(bucket.Width, MinBucketWidth) - Gap;
        for (var y = bucket.Y + Gap; y + NoteHeight <= MaxCoordinate; y += NoteHeight + Gap)
        {
            for (var x = left; x + NoteWidth <= right; x += NoteWidth + Gap)
            {
                if (notes.Any(n => Overlaps(n.X, n.Y, NoteWidth, NoteHeight, x, y))) continue;
                bucket.Height = Math.Min(MaxBucketSize, Math.Max(bucket.Height, y + NoteHeight + Gap - bucket.Y));
                return (x, y);
            }
        }
        return (left, bucket.Y + Gap);
    }

    /// <summary>The first grid slot near the top-left of the board that is clear of every bucket and note.</summary>
    public static (int X, int Y) FreeSpotOutside(IReadOnlyCollection<Bucket> buckets, IReadOnlyCollection<Note> notes)
    {
        for (var y = Gap * 2; y < MaxCoordinate; y += NoteHeight + Gap)
        {
            for (var x = Gap * 2; x < LooseAreaWidth; x += NoteWidth + Gap)
            {
                if (!buckets.Any(b => Overlaps(b.X, b.Y, b.Width, b.Height, x, y))
                    && !notes.Any(n => Overlaps(n.X, n.Y, NoteWidth, NoteHeight, x, y)))
                {
                    return (x, y);
                }
            }
        }
        return (Gap * 2, Gap * 2);
    }

    /// <summary>Where a new image goes when no position is given: to the right of everything on the board.</summary>
    public static (int X, int Y) NextImageSpot(
        IReadOnlyCollection<Bucket> buckets, IReadOnlyCollection<Note> notes, IReadOnlyCollection<BoardImage> images)
    {
        var right = buckets.Select(b => b.X + b.Width)
            .Concat(notes.Select(n => n.X + NoteWidth))
            .Concat(images.Where(i => i.NoteId is null).Select(i => i.X + i.Width))
            .DefaultIfEmpty(0)
            .Max();
        return (Snap(right + Gap * 2), Gap * 2);
    }

    /// <summary>Where a new bucket goes when no position is given: to the right of the existing ones.</summary>
    public static (int X, int Y) NextBucketSpot(IReadOnlyCollection<Bucket> buckets) =>
        (buckets.Count == 0 ? Gap * 2 : buckets.Max(b => b.X + b.Width) + Gap * 2, Gap * 2);

    /// <summary>The size a new block gets when none is given.</summary>
    public static (int Width, int Height) DefaultBlockSize(NoteBlockKind kind) => kind switch
    {
        NoteBlockKind.Checklist => (340, 260),
        NoteBlockKind.Links => (360, 220),
        _ => (340, 220),
    };

    /// <summary>Snaps a block to the grid and keeps it within the limits and on the note's canvas.</summary>
    public static void Clamp(NoteBlock block)
    {
        block.Width = Math.Min(MaxBlockSize, SnapSize(block.Width, MinBlockWidth));
        block.Height = Math.Min(MaxBlockSize, SnapSize(block.Height, MinBlockHeight));
        block.X = Math.Clamp(Snap(block.X), 0, NoteCanvasMax - block.Width);
        block.Y = Math.Clamp(Snap(block.Y), 0, NoteCanvasMax - block.Height);
    }

    /// <summary>A position on a note's canvas, snapped and kept on it.</summary>
    public static (int X, int Y) OnNoteCanvas(int x, int y, int width, int height) =>
        (Math.Clamp(Snap(x), 0, Math.Max(0, NoteCanvasMax - width)), Math.Clamp(Snap(y), 0, Math.Max(0, NoteCanvasMax - height)));

    /// <summary>
    /// The first grid slot on a note's canvas, row by row, where a <paramref name="width"/> by <paramref name="height"/>
    /// rectangle keeps a gap from everything already there (its blocks and attached images).
    /// </summary>
    public static (int X, int Y) NextNoteSpot(IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, int width, int height)
    {
        for (var y = Gap; y + height <= NoteCanvasMax; y += Grid)
        {
            for (var x = Gap; x == Gap || x + width <= BlockAreaWidth; x += Grid)
            {
                if (!taken.Any(t => Intersects(t, (x - Gap, y - Gap, width + 2 * Gap, height + 2 * Gap)))) return (x, y);
            }
        }
        return (Gap, Gap);
    }

    /// <summary>What is on a note's canvas: its blocks, and the images attached to it.</summary>
    public static List<(int X, int Y, int Width, int Height)> TakenOnNote(Note note, IEnumerable<BoardImage> attached) =>
        [.. note.Blocks.Select(b => (b.X, b.Y, b.Width, b.Height)), .. attached.Select(i => (i.X, i.Y, i.Width, i.Height))];

    public static bool Intersects((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>Whether a rectangle overlaps a note-sized rectangle at (<paramref name="noteX"/>, <paramref name="noteY"/>).</summary>
    private static bool Overlaps(int x, int y, int width, int height, int noteX, int noteY) =>
        x < noteX + NoteWidth && noteX < x + width && y < noteY + NoteHeight && noteY < y + height;
}
