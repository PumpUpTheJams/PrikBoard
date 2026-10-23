using System.Net.Http.Json;
using PrikBoard.Api.Contracts;

namespace PrikBoard.Api.Tests;

/// <summary>How buckets stack where they overlap: the same for everyone, kept on the server.</summary>
public class BucketStackingApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task A_bucket_brought_to_front_takes_cards_dropped_where_it_overlaps_and_lists_keep_their_order()
    {
        var client = await factory.CreateSignedInClientAsync("stacking-front@example.com");
        var board = await client.CreateBoardAsync("Front");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        // Doing (on top, as the newer one) lies over To do.
        (await client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { x = todo.X, y = todo.Y })).EnsureSuccessStatusCode();
        var (x, y) = (todo.X + 20, todo.Y + 20);

        var before = await Drop(client, board.Id, x, y);
        var front = await client.PostAsync($"/api/buckets/{todo.Id}/front", null);
        front.EnsureSuccessStatusCode();
        var after = await Drop(client, board.Id, x, y);
        var snapshot = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;

        Assert.Equal(doing.Id, before.BucketId);
        Assert.Equal(todo.Id, after.BucketId);
        // Raising a bucket moves no card between buckets.
        Assert.Equal(doing.Id, snapshot.Cards.Single(n => n.Id == before.Id).BucketId);
        Assert.Equal(["To do", "Doing", "Done"], snapshot.Buckets.Select(b => b.Name));
        Assert.Equal(todo.Id, snapshot.Buckets.MaxBy(b => b.StackedAt)!.Id);
    }

    [Fact]
    public async Task Moving_resizing_opening_and_filling_a_bucket_put_it_on_top_and_renaming_does_not()
    {
        var client = await factory.CreateSignedInClientAsync("stacking-changes@example.com");
        var board = await client.CreateBoardAsync("Changes");
        var (todo, doing, done) = (board.Buckets[0], board.Buckets[1], board.Buckets[2]);
        var card = await client.CreateCardAsync(board.Id);

        async Task<Guid> TopAfter(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            var buckets = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Buckets;
            return buckets.MaxBy(b => b.StackedAt)!.Id;
        }

        Assert.Equal(todo.Id, await TopAfter(await client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = todo.X + 20 })));
        Assert.Equal(doing.Id, await TopAfter(await client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { height = 800 })));
        Assert.Equal(doing.Id, await TopAfter(await client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { name = "Later" })));
        Assert.Equal(doing.Id, await TopAfter(await client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { collapsed = true })));
        Assert.Equal(done.Id, await TopAfter(await client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { collapsed = false })));
        Assert.Equal(todo.Id, await TopAfter(await client.PostAsJsonAsync($"/api/buckets/{todo.Id}/cards", new { cardIds = new[] { card.Id } })));
    }

    [Fact]
    public async Task An_exported_board_imports_with_its_buckets_stacked_and_listed_as_they_were()
    {
        var client = await factory.CreateSignedInClientAsync("stacking-export@example.com");
        var board = await client.CreateBoardAsync("Stacked");
        (await client.PostAsync($"/api/buckets/{board.Buckets[0].Id}/front", null)).EnsureSuccessStatusCode();

        var file = await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();
        var imported = await client.PostAsync("/api/boards/import", new StringContent(file, System.Text.Encoding.UTF8, "application/json"));
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal(["To do", "Doing", "Done"], copy.Buckets.Select(b => b.Name));
        Assert.Equal(["Doing", "Done", "To do"], copy.Buckets.OrderBy(b => b.StackedAt).Select(b => b.Name));
    }

    private static async Task<CardDetail> Drop(HttpClient client, Guid boardId, int x, int y)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/cards", new { title = "Dropped", x, y });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }
}
