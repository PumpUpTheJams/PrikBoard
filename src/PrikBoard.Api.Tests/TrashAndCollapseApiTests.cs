using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The trash (recently deleted cards, which can be put back from there) and buckets that fold up into a square.</summary>
public class TrashAndCollapseApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_trash_lists_deleted_cards_newest_first_with_their_images_and_searches_them()
    {
        var board = await _client.CreateBoardAsync("Trash");
        var first = await _client.CreateCardAsync(board.Id, "Plan the launch party");
        var second = await _client.CreateCardAsync(board.Id, "Book the venue");
        var kept = await _client.CreateCardAsync(board.Id, "Stays on the board");
        var image = await Upload(board.Id, $"?cardId={second.Id}");
        var deletedEarlier = await Upload(board.Id, $"?cardId={second.Id}");
        (await _client.DeleteAsync($"/api/images/{deletedEarlier.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/cards/{first.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/cards/{second.Id}")).EnsureSuccessStatusCode();

        var trash = await Trash(board.Id);
        var found = await Trash(board.Id, "VENUE");

        Assert.Equal([second.Id, first.Id], trash.Cards.Select(n => n.Id));
        Assert.All(trash.Cards, n => Assert.NotNull(n.DeletedAt));
        Assert.Equal(image.Id, Assert.Single(trash.Images).Id); // the one deleted on its own earlier doesn't come back with it
        Assert.Equal(second.Id, Assert.Single(found.Cards).Id);
        Assert.DoesNotContain(trash.Cards, n => n.Id == kept.Id);
        Assert.Null((await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{kept.Id}"))!.DeletedAt);
    }

    [Fact]
    public async Task A_deleted_card_goes_back_where_it_was_or_where_it_is_dropped()
    {
        var board = await _client.CreateBoardAsync("Put back");
        var card = await _client.CreateCardAsync(board.Id, "Back home");
        var dropped = await _client.CreateCardAsync(board.Id, "Somewhere else");
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/cards/{dropped.Id}")).EnsureSuccessStatusCode();

        var half = await _client.PostAsJsonAsync($"/api/cards/{dropped.Id}/restore", new { x = 10 });
        var back = await Ok<CardDetail>(await _client.PostAsync($"/api/cards/{card.Id}/restore", null));
        // Left of and above the origin too: the board has no edge there.
        var elsewhere = await Ok<CardDetail>(await _client.PostAsJsonAsync($"/api/cards/{dropped.Id}/restore", new { x = -3011, y = -409 }));

        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
        Assert.Equal((card.X, card.Y, card.BucketId), (back.X, back.Y, back.BucketId));
        Assert.Equal((-3020, -400, (Guid?)null), (elsewhere.X, elsewhere.Y, elsewhere.BucketId));
        Assert.True(elsewhere.UpdatedAt > dropped.UpdatedAt);
        Assert.Empty((await Trash(board.Id)).Cards);
    }

    [Fact]
    public async Task A_card_in_the_trash_is_deleted_for_good_with_its_images()
    {
        var board = await _client.CreateBoardAsync("Purge");
        var other = await _client.CreateBoardAsync("Purge elsewhere");
        var card = await _client.CreateCardAsync(board.Id, "Gone for good");
        var live = await _client.CreateCardAsync(board.Id, "Still on the board");
        var image = await Upload(board.Id, $"?cardId={card.Id}");
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();

        var notInTrash = await _client.DeleteAsync($"/api/boards/{board.Id}/trash/{live.Id}");
        var wrongBoard = await _client.DeleteAsync($"/api/boards/{other.Id}/trash/{card.Id}");
        var purged = await _client.DeleteAsync($"/api/boards/{board.Id}/trash/{card.Id}");

        Assert.Equal(HttpStatusCode.Conflict, notInTrash.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongBoard.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, purged.StatusCode);
        Assert.Empty((await Trash(board.Id)).Cards);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/cards/{card.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/images/{image.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/cards/{live.Id}")).StatusCode);
    }

    [Fact]
    public async Task Emptying_the_trash_deletes_only_this_boards_deleted_cards_and_needs_an_editor_while_viewers_read_it()
    {
        var board = await _client.CreateBoardAsync("Empty trash");
        var other = await _client.CreateBoardAsync("Keeps its trash");
        var viewer = await factory.CreateSignedInClientAsync("empty-trash-viewer@example.com");
        await _client.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var live = await _client.CreateCardAsync(board.Id, "Stays");
        foreach (var title in new[] { "One", "Two" })
        {
            var card = await _client.CreateCardAsync(board.Id, title);
            (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        }
        var elsewhere = await _client.CreateCardAsync(other.Id, "Other board");
        (await _client.DeleteAsync($"/api/cards/{elsewhere.Id}")).EnsureSuccessStatusCode();

        // Strangers don't see it at all (MembershipApiTests.Visitors_only_read_the_board_its_images_and_drawings).
        var read = await viewer.GetAsync($"/api/boards/{board.Id}/trash");
        var forbidden = await viewer.DeleteAsync($"/api/boards/{board.Id}/trash");
        var result = await Ok<EmptyTrashResult>(await _client.DeleteAsync($"/api/boards/{board.Id}/trash"));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(2, result.Purged);
        Assert.Empty((await Trash(board.Id)).Cards);
        Assert.Equal(elsewhere.Id, Assert.Single((await Trash(other.Id)).Cards).Id);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/cards/{live.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_collapsed_bucket_keeps_its_cards_in_place_and_takes_no_others()
    {
        var board = await _client.CreateBoardAsync("Collapse");
        var toDo = board.Buckets[0];
        var inside = await _client.CreateCardAsync(board.Id, "Inside");

        var collapsed = await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { collapsed = true }));
        // A card put down inside the collapsed bucket's open area stays loose (it would vanish otherwise)...
        var loose = await CreateCard(board.Id, new { title = "Loose", x = toDo.X + 20, y = toDo.Y + 300 });
        // ...but one sent into it by bucketId goes in.
        var sent = await CreateCard(board.Id, new { title = "Sent", bucketId = toDo.Id });
        var resize = await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { width = 600 });
        var moved = await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = toDo.X + 1000 }));
        var opened = await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { collapsed = false }));

        Assert.True(collapsed.Collapsed);
        Assert.Null(loose.BucketId);
        Assert.Equal(toDo.Id, sent.BucketId);
        Assert.Equal(HttpStatusCode.BadRequest, resize.StatusCode);
        Assert.False(opened.Collapsed);
        var after = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        // Its own cards moved along with it while it was collapsed, the loose one didn't.
        Assert.Equal((inside.X + 1000, toDo.Id), after.Cards.Where(n => n.Id == inside.Id).Select(n => (n.X, n.BucketId)).Single());
        Assert.Equal((loose.X, (Guid?)null), after.Cards.Where(n => n.Id == loose.Id).Select(n => (n.X, n.BucketId)).Single());
        Assert.Equal(toDo.X + 1000, moved.X);
    }

    [Fact]
    public async Task A_collapsed_bucket_survives_export_and_import_with_its_cards()
    {
        var board = await _client.CreateBoardAsync("Collapse export");
        var toDo = board.Buckets[0];
        await _client.CreateCardAsync(board.Id, "Inside");
        (await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        var file = await (await _client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();

        var response = await _client.PostAsync("/api/boards/import", new StringContent(file, System.Text.Encoding.UTF8, "application/json"));
        var imported = (await response.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal([true, false, false], imported.Buckets.Select(b => b.Collapsed));
        Assert.Equal(imported.Buckets[0].Id, Assert.Single(imported.Cards).BucketId);
    }

    private async Task<TrashContents> Trash(Guid boardId, string? search = null) =>
        (await _client.GetFromJsonAsync<TrashContents>($"/api/boards/{boardId}/trash{(search is null ? "" : $"?search={search}")}"))!;

    private async Task<CardDetail> CreateCard(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/cards", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<ImageDetail> Upload(Guid boardId, string query = "")
    {
        var content = new ByteArrayContent(TestImages.Png(300, 200)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };
        var response = await _client.PostAsync($"/api/boards/{boardId}/images{query}", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}
