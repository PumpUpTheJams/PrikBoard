using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// A stress test rather than a quick check: thousands of random layouts, which take half a minute, so it only runs with
/// <c>PRIKBOARD_STRESS=1</c> (CI skips it). Run it when changing <see cref="Layout.PackIn"/> or <see cref="Layout.MakeRoomIn"/>.
/// </summary>
public sealed class StressFactAttribute : FactAttribute
{
    public StressFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PRIKBOARD_STRESS") != "1") Skip = "A stress test: set PRIKBOARD_STRESS=1 to run it.";
    }
}

/// <summary>
/// The fast search in <see cref="Layout.PackIn"/> and <see cref="Layout.MakeRoomIn"/> finds the same places, and grows
/// the bucket the same way, as trying every grid step against everything in the way (<see cref="LayoutReference"/>).
/// </summary>
public class LayoutSearchTests
{
    private static readonly GrowSide[] Sides = Enum.GetValues<GrowSide>();

    [StressFact]
    public void PackIn_finds_the_same_places_as_trying_every_step()
    {
        var random = new Random(1234);
        for (var run = 0; run < 2000; run++)
        {
            var bucket = RandomBucket(random);
            var taken = Enumerable.Range(0, random.Next(0, 60)).Select(_ => RandomItem(random, bucket)).ToList();
            var sizes = Enumerable.Range(0, random.Next(1, 60)).Select(_ => RandomSize(random)).ToList();
            var grow = Sides[random.Next(Sides.Length)];

            var expectedBucket = Copy(bucket);
            var expected = LayoutReference.PackIn(expectedBucket, sizes, taken, grow);
            var actual = Layout.PackIn(bucket, sizes, taken, grow);

            Assert.True(expected.SequenceEqual(actual), $"Run {run}: the places differ.");
            Assert.Equal((expectedBucket.X, expectedBucket.Y, expectedBucket.Width, expectedBucket.Height), (bucket.X, bucket.Y, bucket.Width, bucket.Height));
        }
    }

    [StressFact]
    public void MakeRoomIn_finds_the_same_slot_as_trying_every_step()
    {
        var random = new Random(5678);
        var start = DateTimeOffset.UtcNow;
        for (var run = 0; run < 2000; run++)
        {
            var buckets = Enumerable.Range(0, random.Next(1, 5)).Select(i =>
            {
                var b = RandomBucket(random);
                b.Collapsed = random.Next(5) == 0;
                b.StackedAt = start.AddMilliseconds(random.Next(1000));
                return b;
            }).ToList();
            var bucket = buckets[random.Next(buckets.Count)];
            var cards = Enumerable.Range(0, random.Next(0, 80)).Select(_ =>
            {
                var (x, y, _, _) = RandomItem(random, bucket, cardsOnly: true);
                return new Card { Title = "", X = x, Y = y, BucketId = random.Next(3) == 0 ? buckets[random.Next(buckets.Count)].Id : null };
            }).ToList();

            var expectedBucket = Copy(bucket);
            List<Bucket> expectedBuckets = [.. buckets.Select(b => b == bucket ? expectedBucket : b)];
            var expected = LayoutReference.MakeRoomIn(expectedBucket, cards, expectedBuckets);
            var actual = Layout.MakeRoomIn(bucket, cards, buckets);

            Assert.True(expected == actual, $"Run {run}: {expected} expected, {actual} found.");
            Assert.Equal((expectedBucket.X, expectedBucket.Y, expectedBucket.Width, expectedBucket.Height), (bucket.X, bucket.Y, bucket.Width, bucket.Height));
        }
    }

    /// <summary>A bucket on the grid, small to large, now and then near the edge of the board so it can't grow that way.</summary>
    private static Bucket RandomBucket(Random random)
    {
        var nearEdge = random.Next(10) == 0;
        var x = nearEdge ? Layout.MaxCoordinate - random.Next(1, 40) * Layout.Grid : random.Next(-100, 100) * Layout.Grid;
        var y = nearEdge && random.Next(2) == 0 ? Layout.MinCoordinate + random.Next(0, 20) * Layout.Grid : random.Next(-100, 100) * Layout.Grid;
        return new Bucket
        {
            Name = "Random",
            Id = Guid.NewGuid(),
            X = x,
            Y = y,
            Width = random.Next(4) == 0 ? random.Next(100, 1000) : random.Next(12, 70) * Layout.Grid,
            Height = random.Next(4) == 0 ? random.Next(100, 1000) : random.Next(8, 60) * Layout.Grid,
        };
    }

    /// <summary>Something in or near the bucket: a card on the grid, or an image or drawing of any size anywhere.</summary>
    private static (int X, int Y, int Width, int Height) RandomItem(Random random, Bucket near, bool cardsOnly = false)
    {
        var x = near.X + random.Next(-30, Math.Max(near.Width, 300) / Layout.Grid + 30) * Layout.Grid;
        var y = near.Y + random.Next(-30, Math.Max(near.Height, 300) / Layout.Grid + 30) * Layout.Grid;
        if (cardsOnly || random.Next(2) == 0) return (x, y, Layout.CardWidth, Layout.CardHeight);
        return (x + random.Next(-19, 20), y + random.Next(-19, 20), random.Next(0, 900), random.Next(0, 700));
    }

    /// <summary>Mostly cards, sometimes an image or drawing, now and then one wider or taller than the bucket.</summary>
    private static (int Width, int Height) RandomSize(Random random) => random.Next(6) switch
    {
        0 => (random.Next(4, 60) * Layout.Grid, random.Next(1, 600)),
        1 => (random.Next(1, 1500), random.Next(1, 1500)),
        _ => (Layout.CardWidth, Layout.CardHeight),
    };

    private static Bucket Copy(Bucket b) =>
        new() { Name = b.Name, Id = b.Id, X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Collapsed = b.Collapsed, StackedAt = b.StackedAt, CreatedAt = b.CreatedAt };
}
