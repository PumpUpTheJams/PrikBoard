using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// A benchmark rather than a test: it only runs with <c>PRIKBOARD_PERF=1</c> (CI skips it), and prints timings instead of
/// asserting them. See tools/perf/README.md.
/// </summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("PRIKBOARD_PERF") != "1") Skip = "A benchmark: set PRIKBOARD_PERF=1 to run it.";
    }
}

/// <summary>
/// How long the server takes on a big board: the board from tools/perf/make-board.mjs (or the export named by
/// <c>PRIKBOARD_PERF_BOARD</c>), imported once, then read, added to and packed into buckets. With
/// <c>PRIKBOARD_PERF_SNAPSHOT</c> set to a path, it saves the board as getBoard returns it there, for serve-board.mjs.
/// </summary>
public class PerfBenchmarks(PrikBoardApiFactory factory, ITestOutputHelper output) : IClassFixture<PrikBoardApiFactory>
{
    [PerfFact]
    public async Task Server_on_a_big_board()
    {
        var client = await factory.CreateSignedInClientAsync();
        var file = BoardFile();
        var sw = Stopwatch.StartNew();
        var imported = await client.PostAsync("/api/boards/import", new StringContent(await File.ReadAllTextAsync(file), Encoding.UTF8, "application/json"));
        imported.EnsureSuccessStatusCode();
        var board = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;
        Report("importBoard", sw.Elapsed.TotalMilliseconds, $"{board.Cards.Count} cards, {board.Images.Count} images, {board.Drawings.Count} drawings");

        string json = "";
        var read = await Median(async () => json = await client.GetStringAsync($"/api/boards/{board.Id}"));
        Report("getBoard", read, $"{json.Length / 1024} KiB");
        // The board as the web app gets it, for tools/perf/serve-board.mjs.
        if (Environment.GetEnvironmentVariable("PRIKBOARD_PERF_SNAPSHOT") is { Length: > 0 } snapshot) await File.WriteAllTextAsync(snapshot, json);

        var loose = board.Cards.Where(n => n.BucketId is null).Select(n => n.Id).ToList();
        // A small placement first, so the timings below don't include warming up.
        var warmUp = await CreateBucketAsync(client, board.Id, "Warm up", x: -40_000, y: 40_000);
        (await client.PostAsJsonAsync($"/api/buckets/{warmUp.Id}/items", new { cardIds = loose.Take(10) })).EnsureSuccessStatusCode();
        var taken = 10;
        foreach (var (count, grow) in new[] { (100, GrowSide.Down), (200, GrowSide.Down), (180, GrowSide.Right) })
        {
            if (taken + count > loose.Count) break;
            var bucket = await CreateBucketAsync(client, board.Id, $"Packed {count} {grow}", x: -40_000 - taken * 10, y: -40_000);
            var ids = loose.Skip(taken).Take(count).ToList();
            taken += count;
            sw.Restart();
            (await client.PostAsJsonAsync($"/api/buckets/{bucket.Id}/items", new { cardIds = ids, grow = grow.ToString() })).EnsureSuccessStatusCode();
            Report($"placeItemsInBucket {count} cards ({grow})", sw.Elapsed.TotalMilliseconds);
        }

        // Buckets are listed in creation order: the first is the biggest.
        var into = board.Buckets[0];
        sw.Restart();
        for (var batch = 0; batch < 4; batch++)
        {
            var cards = Enumerable.Range(0, 50).Select(i => new { title = $"New {batch * 50 + i}", bucketId = into.Id }).ToList();
            (await client.PostAsJsonAsync($"/api/boards/{board.Id}/cards/batch", new { cards })).EnsureSuccessStatusCode();
        }
        Report("createCards 4 × 50 into a bucket", sw.Elapsed.TotalMilliseconds);

        var move = await Median(async () =>
            (await client.PostAsJsonAsync($"/api/cards/{loose[^1]}/move", new { x = -60_000, y = -60_000 })).EnsureSuccessStatusCode());
        Report("moveCard", move);
    }

    /// <summary>The gap between items in a bucket (Layout.Gap).</summary>
    private const int Gap = 20;

    [PerfFact]
    public void Layout_PackIn()
    {
        var card = (Layout.CardWidth, Layout.CardHeight);
        foreach (var (count, already, grow) in new[]
        {
            (100, 0, GrowSide.Down), (200, 0, GrowSide.Down), (500, 0, GrowSide.Down),
            (100, 40, GrowSide.Down), (100, 40, GrowSide.Right), (200, 100, GrowSide.Down),
        })
        {
            // Cards already in the bucket, two columns from its top-left corner, as a full bucket holds them.
            var taken = Enumerable.Range(0, already)
                .Select(i => (Gap + i % 2 * (Layout.CardWidth + Gap), Gap + i / 2 * (Layout.CardHeight + Gap), Layout.CardWidth, Layout.CardHeight))
                .ToList();
            var sizes = Enumerable.Repeat(card, count).ToList();
            var bucket = new Bucket { Name = "Bench", Width = 460, Height = Math.Max(600, Gap + (already + 1) / 2 * (Layout.CardHeight + Gap)) };
            Layout.PackIn(Copy(bucket), sizes, taken, grow);
            var sw = Stopwatch.StartNew();
            const int runs = 5;
            for (var i = 0; i < runs; i++) Layout.PackIn(Copy(bucket), sizes, taken, grow);
            Report($"Layout.PackIn {count} cards, {already} in the bucket, {grow}", sw.Elapsed.TotalMilliseconds / runs);
        }

        static Bucket Copy(Bucket b) => new() { Name = b.Name, X = b.X, Y = b.Y, Width = b.Width, Height = b.Height };
    }

    private void Report(string what, double ms, string? detail = null) =>
        output.WriteLine($"{what}: {ms:F1} ms{(detail is null ? "" : $" ({detail})")}");

    /// <summary>The median of <paramref name="runs"/> runs, after one to warm up.</summary>
    private static async Task<double> Median(Func<Task> action, int runs = 10)
    {
        await action();
        var times = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        return times.Order().ElementAt(runs / 2);
    }

    private static async Task<BucketDetail> CreateBucketAsync(HttpClient client, Guid boardId, string name, int x, int y)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/buckets", new { name, x, y, width = 460, height = 600 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BucketDetail>())!;
    }

    /// <summary>The export to import: <c>PRIKBOARD_PERF_BOARD</c>, or a fresh one from make-board.mjs (needs Node).</summary>
    private static string BoardFile()
    {
        if (Environment.GetEnvironmentVariable("PRIKBOARD_PERF_BOARD") is { Length: > 0 } given) return given;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "perf", "make-board.mjs"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("tools/perf/make-board.mjs not found; set PRIKBOARD_PERF_BOARD to an export file.");
        var file = Path.Combine(Path.GetTempPath(), $"prikboard-perf-{Guid.NewGuid():N}.json");
        using var node = Process.Start(new ProcessStartInfo("node", ["tools/perf/make-board.mjs", $"--out={file}"])
        {
            WorkingDirectory = dir.FullName,
            RedirectStandardOutput = true,
        })!;
        node.WaitForExit();
        if (node.ExitCode != 0) throw new InvalidOperationException("make-board.mjs failed.");
        return file;
    }
}
