using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Copying cards into a clip (copyItems) and pasting it on a board (pasteItems).</summary>
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
