using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Copying or cutting cards, buckets, images and drawings into a clip (copyItems) and pasting it on a board (pasteItems).</summary>
public class ClipApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    private static readonly double[][] Squiggle = [[2, 2, 0.5], [40, 10, 0.5], [100, 30, 1]];

    [Fact]
    public async Task Copied_cards_paste_on_another_board_with_everything_on_them()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-owner@example.com");
        var me = await owner.MeAsync();
        var from = await owner.CreateBoardAsync("Clip source");
        var to = await owner.CreateBoardAsync("Clip target");
        (await owner.PostAsJsonAsync($"/api/boards/{from.Id}/tags", new { name = "urgent", color = "Brick" })).EnsureSuccessStatusCode();

        var second = await owner.CreateCardAsync(from.Id, "Second");
        var left = await owner.CreateCardAsync(from.Id, "Left behind");
        var first = await CreateRichCardAsync(owner, from.Id, new
        {
            title = "First",
            color = "Sky",
            tags = new[] { "urgent", "plain" },
            assigneeIds = new[] { me.Id },
            x = 400,
            y = 600,
            components = new object[]
            {
                new { kind = "Text", title = "Why", text = "Because." },
                new
                {
                    kind = "Links",
                    links = new[]
                    {
                        new { title = "Web", url = "https://example.com/" },
                        new { title = "Second", url = second.Path },
                        new { title = "Left", url = left.Path },
                    },
                },
            },
        });
        var png = TestImages.Png(300, 100);
        var image = await UploadAsync(owner, from.Id, png, $"?fileName=shot.png&cardId={first.Id}&x=40&y=400&width=200");
        (await owner.PostAsJsonAsync($"/api/boards/{from.Id}/drawings", new { x = 600, y = 400, points = Squiggle, color = "Coral", cardId = first.Id }))
            .EnsureSuccessStatusCode();

        var copied = await owner.PostAsJsonAsync($"/api/boards/{from.Id}/copy", new { cardIds = new[] { first.Id, second.Id } });
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        var clip = (await copied.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("prikboard-clip", (string?)clip["format"]);
        Assert.Equal(from.ShortId, (string?)clip["source"]!["boardShortId"]);
        Assert.DoesNotContain("clip-owner@example.com", clip.ToJsonString());

        var response = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 2000, y = 2000 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pasted = (await response.Content.ReadFromJsonAsync<PastedItems>())!;

        Assert.Equal(2, pasted.Cards.Count);
        var copy = pasted.Cards.Single(n => n.Title == "First");
        var copy2 = pasted.Cards.Single(n => n.Title == "Second");
        Assert.All(pasted.Cards, n => Assert.Equal(to.Id, n.BoardId));
        Assert.Equal([1, 2], pasted.Cards.Select(n => n.Number).Order());
        Assert.Equal(CardColor.Sky, copy.Color);
        Assert.Equal(["urgent", "plain"], copy.Tags);
        Assert.Equal(me.Id, Assert.Single(copy.Assignees).PrincipalId);
        // They keep their places relative to each other, the top-left at 2000, 2000.
        var (minX, minY) = (Math.Min(first.X, second.X), Math.Min(first.Y, second.Y));
        Assert.Equal((2000 + first.X - minX, 2000 + first.Y - minY), (copy.X, copy.Y));
        Assert.Equal((2000 + second.X - minX, 2000 + second.Y - minY), (copy2.X, copy2.Y));

        Assert.Equal("Because.", copy.Components[0].Text);
        Assert.Equal([("Web", "https://example.com/"), ("Second", copy2.Path), ("Left", left.Path)],
            copy.Components[1].Links!.Select(l => (l.Title, l.Url)));

        var pastedImage = Assert.Single(pasted.Images);
        Assert.Equal((copy.Id, "shot.png", image.X, image.Y, image.Width), (pastedImage.CardId, pastedImage.FileName, pastedImage.X, pastedImage.Y, pastedImage.Width));
        Assert.Equal(png, await (await owner.GetAsync(pastedImage.ContentUrl)).Content.ReadAsByteArrayAsync());
        Assert.Equal(copy.Id, Assert.Single(pasted.Drawings).CardId);

        var target = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{to.Id}"))!;
        Assert.Equal(TagColor.Brick, target.Tags.Single(t => t.Name == "urgent").Color);
        Assert.Equal(TagColor.Default, target.Tags.Single(t => t.Name == "plain").Color);

        var history = (await owner.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{copy.Id}/history"))!;
        var entry = Assert.Single(Assert.Single(history).Entries);
        Assert.Equal((CardChangeKind.Copied, first.ReadableId, first.Path), (entry.Kind, entry.Before, entry.After));

        // The originals stay as they were.
        var source = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!;
        Assert.Equal(3, source.Cards.Count);
        Assert.Single(source.Images);
    }

    [Fact]
    public async Task A_clip_from_another_server_links_back_to_it()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-servers@example.com");
        var from = await owner.CreateBoardAsync("Over there");
        var to = await owner.CreateBoardAsync("Over here");
        var other = await owner.CreateCardAsync(from.Id, "Not copied");
        var card = await CreateRichCardAsync(owner, from.Id, new
        {
            title = "Travels",
            components = new object[] { new { kind = "Links", links = new[] { new { title = "Other", url = other.Path } } } },
        });

        var clip = await CopyAsync(owner, from.Id, card.Id);
        clip["source"]!["url"] = "https://other.example";
        var response = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var copy = Assert.Single((await response.Content.ReadFromJsonAsync<PastedItems>())!.Cards);

        Assert.Equal($"https://other.example{other.Path}", Assert.Single(copy.Components[0].Links!).Url);
        var history = (await owner.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{copy.Id}/history"))!;
        Assert.Equal($"https://other.example{card.Path}", Assert.Single(Assert.Single(history).Entries).After);
    }

    [Fact]
    public async Task Without_a_place_cards_go_into_the_first_bucket_or_the_one_named()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-buckets@example.com");
        var board = await owner.CreateBoardAsync("Buckets");
        var card = await owner.CreateCardAsync(board.Id, "Again");
        var clip = await CopyAsync(owner, board.Id, card.Id);

        var toDo = board.Buckets.Single(b => b.Name == "To do");
        var pasted = await PasteAsync(owner, board.Id, new { clip });
        Assert.Equal(toDo.Id, Assert.Single(pasted.Cards).BucketId);
        Assert.Equal(toDo.Id, pasted.Bucket!.Id);

        var done = board.Buckets.Single(b => b.Name == "Done");
        pasted = await PasteAsync(owner, board.Id, new { clip, bucketId = done.Id });
        Assert.Equal(done.Id, Assert.Single(pasted.Cards).BucketId);

        var bad = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip, bucketId = done.Id, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("bucketId", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewers_can_copy_but_not_paste_and_outsiders_can_do_neither()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-roles-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("clip-roles-viewer@example.com");
        var outsider = await factory.CreateSignedInClientAsync("clip-roles-outsider@example.com");
        var board = await owner.CreateBoardAsync("Roles");
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var card = await owner.CreateCardAsync(board.Id);

        var clip = await CopyAsync(viewer, board.Id, card.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip, x = 0, y = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/boards/{board.Id}/copy", new { cardIds = new[] { card.Id } })).StatusCode);

        // A card from another board can't be copied from this one.
        var elsewhere = await owner.CreateBoardAsync("Elsewhere");
        var stranger = await owner.CreateCardAsync(elsewhere.Id);
        var wrong = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/copy", new { cardIds = new[] { stranger.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("cardIds", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_clip_from_a_newer_PrikBoard_is_refused()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-newer@example.com");
        var board = await owner.CreateBoardAsync("Newer");
        var card = await owner.CreateCardAsync(board.Id);

        var newer = await CopyAsync(owner, board.Id, card.Id);
        newer["version"] = 99;
        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip = newer, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("clip.version", await response.Content.ReadAsStringAsync());

        var unknown = await CopyAsync(owner, board.Id, card.Id);
        unknown["cards"]![0]!["priority"] = "high";
        response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip = unknown, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("clip.cards[0].priority", await response.Content.ReadAsStringAsync());

        var board2 = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Single(board2.Cards);
    }

    [Fact]
    public async Task Buckets_paste_with_what_is_in_them_and_loose_items_keep_their_places()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-buckets-items@example.com");
        var from = await owner.CreateBoardAsync("Bucket source");
        var to = await owner.CreateBoardAsync("Bucket target");

        var sprint = await CreateBucketAsync(owner, from.Id, new { name = "Sprint", x = 2000, y = 0, width = 600, height = 400 });
        var folded = await CreateBucketAsync(owner, from.Id, new { name = "Later", x = 2800, y = 0, width = 400, height = 400 });
        var inSprint = await CreateRichCardAsync(owner, from.Id, new { title = "In the sprint", bucketId = sprint.Id });
        await CreateRichCardAsync(owner, from.Id, new { title = "For later", bucketId = folded.Id });
        var imageIn = await UploadAsync(owner, from.Id, TestImages.Png(200, 100), "?fileName=in.png&x=2300&y=200&width=200");
        var imageOut = await UploadAsync(owner, from.Id, TestImages.Png(200, 100), "?fileName=out.png&x=3400&y=100&width=200");
        (await owner.PostAsJsonAsync($"/api/boards/{from.Id}/drawings", new { x = 2040, y = 300, points = Squiggle })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/buckets/{folded.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        // Loose items on the target board stay loose under the pasted buckets: only a bucket drawn around items takes them.
        var underSprint = await CreateRichCardAsync(owner, to.Id, new { title = "Under the sprint", x = 300, y = 3200 });
        var underFolded = await CreateRichCardAsync(owner, to.Id, new { title = "Under later", x = 840, y = 3040 });

        var copied = await owner.PostAsJsonAsync($"/api/boards/{from.Id}/copy",
            new { bucketIds = new[] { sprint.Id, folded.Id }, imageIds = new[] { imageOut.Id } });
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        var clip = (await copied.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(BoardClip.CurrentVersion, (int)clip["version"]!);

        // The clip's top-left (Sprint's corner) goes to 0, 3000.
        var pasted = await PasteAsync(owner, to.Id, new { clip, x = 0, y = 3000 });
        Assert.Null(pasted.Bucket);
        Assert.Equal(["Sprint", "Later"], pasted.Buckets.Select(b => b.Name));
        var newSprint = pasted.Buckets[0];
        var newFolded = pasted.Buckets[1];
        Assert.Equal((0, 3000, 600, 400, false), (newSprint.X, newSprint.Y, newSprint.Width, newSprint.Height, newSprint.Collapsed));
        Assert.Equal((800, 3000, true), (newFolded.X, newFolded.Y, newFolded.Collapsed));
        Assert.Equal(newSprint.Id, pasted.Cards.Single(n => n.Title == "In the sprint").BucketId);
        Assert.Equal(newFolded.Id, pasted.Cards.Single(n => n.Title == "For later").BucketId);
        Assert.Equal((inSprint.X - 2000, inSprint.Y + 3000),
            (pasted.Cards.Single(n => n.Title == "In the sprint").X, pasted.Cards.Single(n => n.Title == "In the sprint").Y));

        var newIn = pasted.Images.Single(i => i.FileName == "in.png");
        var newOut = pasted.Images.Single(i => i.FileName == "out.png");
        Assert.Equal((newSprint.Id, imageIn.X - 2000, imageIn.Y + 3000), (newIn.BucketId, newIn.X, newIn.Y));
        Assert.Equal((null, imageOut.X - 2000, imageOut.Y + 3000), (newOut.BucketId, newOut.X, newOut.Y));
        var drawing = Assert.Single(pasted.Drawings);
        Assert.Equal(newSprint.Id, drawing.BucketId);
        Assert.Equal((40, 3300), (drawing.X, drawing.Y));

        var board = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{to.Id}"))!;
        Assert.Null(board.Cards.Single(n => n.Id == underSprint.Id).BucketId);
        Assert.Null(board.Cards.Single(n => n.Id == underFolded.Id).BucketId);
        // Pasted buckets are on top of the board's own.
        Assert.True(board.Buckets.Where(b => b.Id != newSprint.Id && b.Id != newFolded.Id).All(b => b.StackedAt < newSprint.StackedAt));
        Assert.True(newSprint.StackedAt < newFolded.StackedAt);

        // As version 3 wrote them, with a color on each bucket: the color is ignored.
        clip["version"] = 3;
        foreach (var bucket in clip["buckets"]!.AsArray()) bucket!["color"] = "Teal";
        Assert.Equal(2, (await PasteAsync(owner, to.Id, new { clip, x = 4000, y = 3000 })).Buckets.Count);
    }

    [Fact]
    public async Task A_clip_with_buckets_or_loose_items_does_not_go_into_a_bucket_and_a_version_1_clip_still_pastes()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-v1@example.com");
        var board = await owner.CreateBoardAsync("Versions");
        var image = await UploadAsync(owner, board.Id, TestImages.Png(100, 100), "?fileName=loose.png&x=2000&y=2000");
        var withImage = (await (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/copy", new { imageIds = new[] { image.Id } })).Content.ReadFromJsonAsync<JsonObject>())!;
        var toDo = board.Buckets.Single(b => b.Name == "To do");
        var refused = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/paste", new { clip = withImage, bucketId = toDo.Id });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("bucketId", await refused.Content.ReadAsStringAsync());
        // Without a place it goes to a free spot.
        var placed = await PasteAsync(owner, board.Id, new { clip = withImage });
        Assert.Equal("loose.png", Assert.Single(placed.Images).FileName);

        // As the first clip version was written: version 1, no buckets, no bucketId on items.
        var card = await owner.CreateCardAsync(board.Id, "Old style");
        var old = await CopyAsync(owner, board.Id, card.Id);
        old["version"] = 1;
        old.Remove("buckets");
        foreach (var item in old["cards"]!.AsArray()) item!.AsObject().Remove("bucketId");
        var pasted = await PasteAsync(owner, board.Id, new { clip = old, x = 0, y = 0 });
        Assert.Equal("Old style", Assert.Single(pasted.Cards).Title);
        Assert.Empty(pasted.Buckets);
    }

    [Fact]
    public async Task Cut_items_move_to_another_board_and_the_originals_go_to_the_trash()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-cut@example.com");
        var from = await owner.CreateBoardAsync("Cut from");
        var to = await owner.CreateBoardAsync("Cut to");
        var bucket = await CreateBucketAsync(owner, from.Id, new { name = "Moving", x = 2000, y = 0, width = 600, height = 400 });
        var inBucket = await CreateRichCardAsync(owner, from.Id, new { title = "In it", bucketId = bucket.Id });
        var loose = await CreateRichCardAsync(owner, from.Id, new { title = "Loose", x = 2800, y = 0 });
        await UploadAsync(owner, from.Id, TestImages.Png(100, 100), $"?fileName=on-card.png&cardId={loose.Id}");
        var image = await UploadAsync(owner, from.Id, TestImages.Png(100, 100), "?fileName=board.png&x=3200&y=0");

        var response = await owner.PostAsJsonAsync($"/api/boards/{from.Id}/copy",
            new { cardIds = new[] { loose.Id }, bucketIds = new[] { bucket.Id }, imageIds = new[] { image.Id }, cut = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var clip = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True((bool)clip["cut"]!);
        // Added to the bucket after the cut: not part of it, so it stays behind.
        var later = await CreateRichCardAsync(owner, from.Id, new { title = "Later", bucketId = bucket.Id });

        var pasted = await PasteAsync(owner, to.Id, new { clip, x = 0, y = 3000 });
        Assert.Equal(["In it", "Loose"], pasted.Cards.Select(n => n.Title).Order());
        var moved = Assert.IsType<MovedOriginals>(pasted.Moved);
        Assert.Equal((from.Id, "Cut from"), (moved.BoardId, moved.BoardName));
        Assert.Equal([inBucket.Id, loose.Id], moved.Cards.Select(n => n.Id).Order());
        Assert.Equal(bucket.Id, Assert.Single(moved.Buckets).Id);
        Assert.Equal(image.Id, Assert.Single(moved.Images).Id);

        var source = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!;
        Assert.Equal(later.Id, Assert.Single(source.Cards).Id);
        Assert.Null(source.Cards[0].BucketId);
        Assert.DoesNotContain(source.Buckets, b => b.Id == bucket.Id);
        Assert.Empty(source.Images);
        var trash = (await owner.GetFromJsonAsync<TrashContents>($"/api/boards/{from.Id}/trash"))!;
        Assert.Equal([inBucket.Id, loose.Id], trash.Cards.Select(n => n.Id).Order());

        var copy = pasted.Cards.Single(n => n.Title == "Loose");
        var copyHistory = (await owner.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{copy.Id}/history"))!;
        Assert.Equal((CardChangeKind.Moved, loose.ReadableId), (Assert.Single(Assert.Single(copyHistory).Entries).Kind, copyHistory[0].Entries[0].Before));
        var originalHistory = (await owner.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{loose.Id}/history"))!;
        var away = originalHistory[0].Entries.Single();
        Assert.Equal((CardChangeKind.MovedAway, copy.ReadableId, copy.Path), (away.Kind, away.Before, away.After));

        // Pasting the same cut again moves nothing more.
        var again = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("moved already", await again.Content.ReadAsStringAsync());

        // Undo brings the originals back with the restore endpoints, with the card's image.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/cards/{loose.Id}/restore", null)).StatusCode);
        var restored = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!;
        Assert.Contains(restored.Images, i => i.CardId == loose.Id);
    }

    [Fact]
    public async Task A_cut_needs_the_Editor_role_on_both_boards_and_another_board_or_server()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-cut-rules@example.com");
        var viewer = await factory.CreateSignedInClientAsync("clip-cut-viewer@example.com");
        var from = await owner.CreateBoardAsync("Cut rules from");
        var to = await owner.CreateBoardAsync("Cut rules to");
        var card = await owner.CreateCardAsync(from.Id, "Stays");
        await owner.ShareAsync(from.Id, viewer, BoardRole.Viewer);

        var refused = await viewer.PostAsJsonAsync($"/api/boards/{from.Id}/copy", new { cardIds = new[] { card.Id }, cut = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("\"cut\"", await refused.Content.ReadAsStringAsync());

        var cut = (await (await owner.PostAsJsonAsync($"/api/boards/{from.Id}/copy", new { cardIds = new[] { card.Id }, cut = true }))
            .Content.ReadFromJsonAsync<JsonObject>())!;
        var sameBoard = await owner.PostAsJsonAsync($"/api/boards/{from.Id}/paste", new { clip = cut, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, sameBoard.StatusCode);
        Assert.Contains("clip.cut", await sameBoard.Content.ReadAsStringAsync());

        // Pasting the cut on the viewer's own board is refused too: they can't take the card off a board they only view.
        var own = await viewer.CreateBoardAsync("Viewer's own");
        var notTheirs = await viewer.PostAsJsonAsync($"/api/boards/{own.Id}/paste", new { clip = cut, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, notTheirs.StatusCode);
        Assert.Contains("clip.cut", await notTheirs.Content.ReadAsStringAsync());

        // From another server: a copy, and the original stays.
        var elsewhere = cut.DeepClone().AsObject();
        elsewhere["source"]!["url"] = "https://other.example";
        var copied = await PasteAsync(owner, to.Id, new { clip = elsewhere, x = 0, y = 0 });
        Assert.Null(copied.Moved);
        Assert.Single((await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!.Cards);
    }

    private static async Task<BucketDetail> CreateBucketAsync(HttpClient client, Guid boardId, object bucket)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/buckets", bucket);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BucketDetail>())!;
    }

    private static async Task<CardDetail> CreateRichCardAsync(HttpClient client, Guid boardId, object card)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/cards", card);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private static async Task<JsonObject> CopyAsync(HttpClient client, Guid boardId, params Guid[] cardIds)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/copy", new { cardIds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<PastedItems> PasteAsync(HttpClient client, Guid boardId, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/paste", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PastedItems>())!;
    }

    private static async Task<ImageDetail> UploadAsync(HttpClient client, Guid boardId, byte[] bytes, string query)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        var response = await client.PostAsync($"/api/boards/{boardId}/images{query}", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}
