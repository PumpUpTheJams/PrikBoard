using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Deleting and restoring many items at once (deleteItems, restoreItems), as the web app's selection and undo do.</summary>
public class ItemApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    private static readonly double[][] Squiggle = [[2, 2, 0.5], [40, 10, 0.5], [100, 30, 1]];

    [Fact]
    public async Task A_selection_is_deleted_and_restored_in_one_go_as_one_by_one()
    {
        var owner = await factory.CreateSignedInClientAsync("items-owner@example.com");
        var board = await owner.CreateBoardAsync("Items");
        var bucket = await CreateAsync<BucketDetail>(owner, $"/api/boards/{board.Id}/buckets", new { name = "Sprint", x = 2000, y = 0, width = 600, height = 400 });
        var inBucket = await CreateAsync<CardDetail>(owner, $"/api/boards/{board.Id}/cards", new { title = "Stays", bucketId = bucket.Id });
        var loose = await CreateAsync<CardDetail>(owner, $"/api/boards/{board.Id}/cards", new { title = "Loose", x = 4000, y = 4000 });
        var blank = await CreateAsync<CardDetail>(owner, $"/api/boards/{board.Id}/cards", new { title = "", x = 4400, y = 4000 });
        var drawing = await CreateAsync<DrawingDetail>(owner, $"/api/boards/{board.Id}/drawings", new { x = 5000, y = 5000, points = Squiggle });
        var image = await UploadAsync(owner, board.Id, "?x=6000&y=6000");
        var onCard = await UploadAsync(owner, board.Id, $"?cardId={loose.Id}");

        var deleted = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new
        {
            cardIds = new[] { loose.Id, blank.Id },
            bucketIds = new[] { bucket.Id },
            imageIds = new[] { image.Id },
            drawingIds = new[] { drawing.Id },
        });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var after = await BoardAsync(owner, board.Id);
        Assert.Equal([inBucket.Id], after.Cards.Select(n => n.Id));
        // What was in the bucket stays, loose.
        Assert.Null(after.Cards[0].BucketId);
        Assert.DoesNotContain(after.Buckets, b => b.Id == bucket.Id);
        Assert.Empty(after.Images);
        Assert.Empty(after.Drawings);
        // The blank card went for good; the other one is in the trash with its image.
        var trash = (await owner.GetFromJsonAsync<TrashContents>($"/api/boards/{board.Id}/trash"))!;
        Assert.Equal([loose.Id], trash.Cards.Select(n => n.Id));
        Assert.Equal([onCard.Id], trash.Images.Select(i => i.Id));

        var restored = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", new
        {
            cardIds = new[] { loose.Id },
            bucketIds = new[] { bucket.Id },
            imageIds = new[] { image.Id },
            drawingIds = new[] { drawing.Id },
        });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var back = (await restored.Content.ReadFromJsonAsync<RestoredItems>())!;
        Assert.Equal([loose.Id], back.Cards.Select(n => n.Id));
        var now = await BoardAsync(owner, board.Id);
        Assert.Contains(now.Buckets, b => b.Id == bucket.Id);
        Assert.Equal((loose.X, loose.Y), now.Cards.Where(n => n.Id == loose.Id).Select(n => (n.X, n.Y)).Single());
        Assert.Equal([image.Id, onCard.Id], now.Images.Select(i => i.Id).Order());
        Assert.Equal([drawing.Id], now.Drawings.Select(d => d.Id));
        // A restored bucket takes nothing, as restoreBucket.
        Assert.Null(now.Cards.Single(n => n.Id == inBucket.Id).BucketId);

        // Restoring what is back already changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", new { cardIds = new[] { loose.Id } })).StatusCode);
        Assert.Equal(["Created", "Deleted", "Restored"],
            (await owner.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{loose.Id}/history"))!
                .SelectMany(c => c.Entries).Select(e => e.Kind.ToString()).Where(k => k is "Created" or "Deleted" or "Restored").Reverse());
    }

    [Fact]
    public async Task Cards_deleted_with_their_bucket_come_back_in_it()
    {
        var owner = await factory.CreateSignedInClientAsync("items-bucket@example.com");
        var board = await owner.CreateBoardAsync("Items bucket");
        var bucket = await CreateAsync<BucketDetail>(owner, $"/api/boards/{board.Id}/buckets", new { name = "Pasted", x = 2000, y = 0, width = 600, height = 400 });
        var card = await CreateAsync<CardDetail>(owner, $"/api/boards/{board.Id}/cards", new { title = "In it", bucketId = bucket.Id });
        var drawing = await CreateAsync<DrawingDetail>(owner, $"/api/boards/{board.Id}/drawings", new { x = 2040, y = 200, points = Squiggle });
        Assert.Equal(bucket.Id, drawing.BucketId);

        // As undoing a paste does: everything it added at once, then back again.
        var items = new { cardIds = new[] { card.Id }, bucketIds = new[] { bucket.Id }, drawingIds = new[] { drawing.Id } };
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", items)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", items)).StatusCode);
        var now = await BoardAsync(owner, board.Id);
        Assert.Equal(bucket.Id, now.Cards.Single(n => n.Id == card.Id).BucketId);
        Assert.Equal(bucket.Id, now.Drawings.Single(d => d.Id == drawing.Id).BucketId);
    }

    [Fact]
    public async Task Items_must_be_on_the_board_and_need_an_Editor()
    {
        var owner = await factory.CreateSignedInClientAsync("items-rules@example.com");
        var viewer = await factory.CreateSignedInClientAsync("items-viewer@example.com");
        var board = await owner.CreateBoardAsync("Items rules");
        var other = await owner.CreateBoardAsync("Items other");
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var card = await owner.CreateCardAsync(board.Id, "Here");
        var elsewhere = await owner.CreateCardAsync(other.Id, "There");

        var mixed = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { cardIds = new[] { card.Id, elsewhere.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Contains("cardIds", await mixed.Content.ReadAsStringAsync());
        Assert.Equal(2, (await BoardAsync(owner, board.Id)).Cards.Count + (await BoardAsync(owner, other.Id)).Cards.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { cardIds = new[] { card.Id } })).StatusCode);

        // An image on a deleted card comes back with its card only.
        var image = await UploadAsync(owner, board.Id, $"?cardId={card.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { imageIds = new[] { image.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/delete", new { cardIds = new[] { card.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", new { imageIds = new[] { image.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/items/restore", new { cardIds = new[] { card.Id }, imageIds = new[] { image.Id } })).StatusCode);
        Assert.Equal([image.Id], (await BoardAsync(owner, board.Id)).Images.Select(i => i.Id));
    }

    private static async Task<T> CreateAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<BoardDetail> BoardAsync(HttpClient client, Guid boardId) =>
        (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{boardId}"))!;

    private static async Task<ImageDetail> UploadAsync(HttpClient client, Guid boardId, string query)
    {
        var content = new ByteArrayContent(TestImages.Png(100, 100));
        content.Headers.ContentType = new("image/png");
        var response = await client.PostAsync($"/api/boards/{boardId}/images{query}", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}
