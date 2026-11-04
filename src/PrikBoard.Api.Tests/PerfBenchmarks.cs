using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
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
/// How long the server takes on a big board: the clip from tools/perf/make-board.mjs (or the one named by
/// <c>PRIKBOARD_PERF_BOARD</c>), pasted into a new board as the web app pastes a big one (images uploaded first), then
/// read, added to and packed into buckets. With <c>PRIKBOARD_PERF_SNAPSHOT</c> set to a path, it saves the board as
/// getBoard returns it there, for serve-board.mjs.
/// </summary>
public class PerfBenchmarks(PrikBoardApiFactory factory, ITestOutputHelper output) : IClassFixture<PrikBoardApiFactory>
{
    [PerfFact]
    public async Task Server_on_a_big_board()
    {
        var client = await factory.CreateSignedInClientAsync();
        var clip = JsonNode.Parse(await File.ReadAllTextAsync(BoardFile()))!.AsObject();
        var board = await client.CreateBoardAsync("Perf");
        // Its top-left where it was, so the layout stays as make-board.mjs drew it.
        var corners = new[] { "cards", "buckets", "images", "drawings", "labels" }.SelectMany(list => clip[list]?.AsArray() ?? [])
            .Where(IsOnBoard).ToList();
        var (x, y) = (corners.Min(c => (int)c!["x"]!), corners.Min(c => (int)c!["y"]!));

        var sw = Stopwatch.StartNew();
        var images = clip["images"]!.AsArray();
        var tokens = new JsonArray();
        foreach (var image in images)
        {
            var content = new ByteArrayContent(Convert.FromBase64String((string)image!["data"]!));
            content.Headers.ContentType = new("application/octet-stream");
            var uploaded = await client.PostAsync($"/api/boards/{board.Id}/paste-images", content);
            uploaded.EnsureSuccessStatusCode();
            tokens.Add((await uploaded.Content.ReadFromJsonAsync<PasteImageFile>())!.Token);
            image["data"] = "";
        }
        Report($"uploadPasteImage × {images.Count}", sw.Elapsed.TotalMilliseconds);

        sw.Restart();
        (await client.PostAsJsonAsync($"/api/boards/{board.Id}/paste/check", new { clip, x, y })).EnsureSuccessStatusCode();
        Report("checkPaste", sw.Elapsed.TotalMilliseconds);
        sw.Restart();
        var pasted = await client.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip, x, y, imageFiles = tokens });
        pasted.EnsureSuccessStatusCode();
        var added = (await pasted.Content.ReadFromJsonAsync<PastedItems>())!;
        Report("pasteItems", sw.Elapsed.TotalMilliseconds, $"{added.Cards.Count} cards, {added.Images.Count} images, {added.Drawings.Count} drawings");
        board = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;

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

    /// <summary>Whether a clip's item sits on the board, not on a card's canvas.</summary>
    private static bool IsOnBoard(JsonNode? item) => item?["cardId"] is null;

    /// <summary>
    /// Many labels: <c>PRIKBOARD_PERF_LABELS_BOARD</c>, or make-board.mjs with <c>--labels=3000</c>. What a label's own requests
    /// cost on a board full of them, and what they add to reading the board: allocations too, which swing less than times.
    /// </summary>
    [PerfFact]
    public async Task Server_with_many_labels()
    {
        var client = await factory.CreateSignedInClientAsync();
        var clip = JsonNode.Parse(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("PRIKBOARD_PERF_LABELS_BOARD") is { Length: > 0 } given ? given : MadeBoard("--labels=3000", "--images=0", "--drawings=0")))!.AsObject();
        var board = await client.CreateBoardAsync("Perf labels");
        var corners = new[] { "cards", "buckets", "labels" }.SelectMany(list => clip[list]?.AsArray() ?? []).Where(IsOnBoard).ToList();
        var (x, y) = (corners.Min(c => (int)c!["x"]!), corners.Min(c => (int)c!["y"]!));

        var sw = Stopwatch.StartNew();
        var pasted = await client.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip, x, y });
        var body = await pasted.Content.ReadAsStringAsync();
        Assert.True(pasted.IsSuccessStatusCode, body);
        Report("pasteItems", sw.Elapsed.TotalMilliseconds, $"{clip["labels"]!.AsArray().Count} labels in the clip");

        string json = "";
        var read = await Median(async () => json = await client.GetStringAsync($"/api/boards/{board.Id}"));
        Report("getBoard", read, $"{json.Length / 1024} KiB");
        var before = GC.GetTotalAllocatedBytes(true);
        await client.GetStringAsync($"/api/boards/{board.Id}");
        Report("getBoard allocations", (GC.GetTotalAllocatedBytes(true) - before) / 1024d / 1024d, "MiB, shown as ms");
        if (Environment.GetEnvironmentVariable("PRIKBOARD_PERF_SNAPSHOT") is { Length: > 0 } snapshot) await File.WriteAllTextAsync(snapshot, json);

        board = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        var looseLabels = board.Labels.Where(l => l.CardId is null && l.BucketId is null).ToList();
        var inBucket = board.Labels.Where(l => l.BucketId is not null).ToList();
        var card = board.Cards[0];
        Report("labels on the board", board.Labels.Count, $"{looseLabels.Count} loose, {inBucket.Count} in a bucket, {board.Labels.Count(l => l.CardId is not null)} on cards");

        // A board without them (--labels=0) only gives the reads above to compare with.
        if (looseLabels.Count == 0) return;

        Report("createLabel at a place", await Median(async () =>
            (await client.PostAsJsonAsync($"/api/boards/{board.Id}/labels", new { markdown = "A new label", x = -50_000, y = -50_000 })).EnsureSuccessStatusCode()));
        Report("createLabel in a free spot", await Median(async () =>
            (await client.PostAsJsonAsync($"/api/boards/{board.Id}/labels", new { markdown = "A new label" })).EnsureSuccessStatusCode()));
        Report("createLabel on a card", await Median(async () =>
            (await client.PostAsJsonAsync($"/api/boards/{board.Id}/labels", new { markdown = "On a card", cardId = card.Id })).EnsureSuccessStatusCode(), runs: 5));

        var one = looseLabels[0];
        Report("updateLabel text", await Median(async () =>
            (await client.PatchAsJsonAsync($"/api/labels/{one.Id}", new { markdown = "## Changed\n\nSome **new** text." })).EnsureSuccessStatusCode()));
        Report("updateLabel move", await Median(async () =>
            (await client.PatchAsJsonAsync($"/api/labels/{one.Id}", new { x = one.X + 20, y = one.Y })).EnsureSuccessStatusCode()));
        Report("updateLabel box", await Median(async () =>
            (await client.PatchAsJsonAsync($"/api/labels/{one.Id}", new { wrapWidth = 300, minHeight = 200, width = 300, height = 200 })).EnsureSuccessStatusCode()));

        if (inBucket.Count > 0)
        {
            var bucket = board.Buckets.Single(b => b.Id == inBucket[0].BucketId);
            var step = 0;
            Report($"updateBucket carrying {inBucket.Count} labels", await Median(async () =>
                (await client.PatchAsJsonAsync($"/api/buckets/{bucket.Id}", new { x = bucket.X + 20 * (++step % 2), y = bucket.Y })).EnsureSuccessStatusCode(), runs: 5));
        }

        var someIds = looseLabels.Skip(1).Take(500).Select(l => l.Id).ToList();
        sw.Restart();
        (await client.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { labelIds = someIds })).EnsureSuccessStatusCode();
        Report($"deleteItems {someIds.Count} labels", sw.Elapsed.TotalMilliseconds);
        sw.Restart();
        (await client.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", new { labelIds = someIds })).EnsureSuccessStatusCode();
        Report($"restoreItems {someIds.Count} labels", sw.Elapsed.TotalMilliseconds);

        Report("listCards search through label text", await Median(async () =>
            await client.GetStringAsync($"/api/boards/{board.Id}/cards?search=pointer"), runs: 5));
        sw.Restart();
        var copied = await client.PostAsJsonAsync($"/api/boards/{board.Id}/copy", new { labelIds = looseLabels.Select(l => l.Id) });
        copied.EnsureSuccessStatusCode();
        Report($"copyItems {looseLabels.Count} labels", sw.Elapsed.TotalMilliseconds);
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

    /// <summary>The clip to paste: <c>PRIKBOARD_PERF_BOARD</c>, or a fresh one from make-board.mjs (needs Node).</summary>
    private static string BoardFile() =>
        Environment.GetEnvironmentVariable("PRIKBOARD_PERF_BOARD") is { Length: > 0 } given ? given : MadeBoard();

    /// <summary>A fresh board from make-board.mjs (needs Node), with these options.</summary>
    private static string MadeBoard(params string[] options)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "perf", "make-board.mjs"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("tools/perf/make-board.mjs not found; set PRIKBOARD_PERF_BOARD to a clip file.");
        var file = Path.Combine(Path.GetTempPath(), $"prikboard-perf-{Guid.NewGuid():N}.json");
        using var node = Process.Start(new ProcessStartInfo("node", ["tools/perf/make-board.mjs", $"--out={file}", .. options])
        {
            WorkingDirectory = dir.FullName,
            RedirectStandardOutput = true,
        })!;
        node.WaitForExit();
        if (node.ExitCode != 0) throw new InvalidOperationException("make-board.mjs failed.");
        return file;
    }
}
