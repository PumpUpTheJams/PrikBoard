using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class PlaceCardsApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Cards_fill_the_free_spots_around_what_is_in_the_bucket()
    {
        var board = await _client.CreateBoardAsync("Fill");
        var todo = board.Buckets[0];
        var there = await CreateCard(board.Id, new { title = "There", bucketId = todo.Id });
        var image = await _client.PostAsync($"/api/boards/{board.Id}/images?x=60&y=200&width=160",
            new ByteArrayContent(TestImages.Png(160, 100)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } });
        image.EnsureSuccessStatusCode();
        var first = await CreateCard(board.Id, new { title = "First", x = 1000, y = 1000 });
        var second = await CreateCard(board.Id, new { title = "Second", x = 1300, y = 1000 });

        var placed = await Place(todo.Id, new { cardIds = new[] { first.Id, second.Id } });

        Assert.Equal((60, 60), (there.X, there.Y));
        Assert.Equal([(60, 340), (60, 480)], placed.Cards.Select(n => (n.X, n.Y)));
        Assert.All(placed.Cards, n => Assert.Equal(todo.Id, n.BucketId));
        Assert.Equal((todo.X, todo.Y, todo.Width, todo.Height), (placed.Bucket.X, placed.Bucket.Y, placed.Bucket.Width, placed.Bucket.Height));
    }

    // To do grows left, up and down; Done, with nothing to its right, grows right (towards Doing, stacked above To do,
    // it would have to grow past it).
    [Theory]
    [InlineData("Down", 0, 40, 40, 240, 720)]
    [InlineData("Right", 2, 600, 40, 460, 600)]
    [InlineData("Left", 0, -180, 40, 460, 600)]
    [InlineData("Up", 0, 40, -100, 240, 740)]
    public async Task A_full_bucket_grows_towards_the_side_asked(string grow, int bucket, int x, int y, int width, int height)
    {
        var board = await _client.CreateBoardAsync($"Grow {grow}");
        var todo = board.Buckets[bucket];
        var cards = new List<CardDetail>();
        for (var i = 0; i < 5; i++) cards.Add(await CreateCard(board.Id, new { title = $"Card {i}", x = 1000 + 220 * i, y = 1000 }));

        var placed = await Place(todo.Id, new { cardIds = cards.Select(n => n.Id), grow });

        Assert.Equal((x, y, width, height), (placed.Bucket.X, placed.Bucket.Y, placed.Bucket.Width, placed.Bucket.Height));
        Assert.Equal(5, placed.Cards.Select(n => (n.X, n.Y)).Distinct().Count());
        Assert.All(placed.Cards, n =>
        {
            Assert.Equal(todo.Id, n.BucketId);
            Assert.InRange(n.X, x, x + width - Layout.CardWidth);
            Assert.InRange(n.Y, y, y + height - Layout.CardHeight);
        });
    }

    [Fact]
    public async Task A_collapsed_bucket_takes_them_out_of_sight()
    {
        var board = await _client.CreateBoardAsync("Folded");
        var done = board.Buckets[2];
        (await _client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        var card = await CreateCard(board.Id, new { title = "Finished", x = 1000, y = 1000 });

        var placed = await Place(done.Id, new { cardIds = new[] { card.Id } });

        Assert.Equal((done.Id, 620, 60), (placed.Cards[0].BucketId, placed.Cards[0].X, placed.Cards[0].Y));
    }

    [Fact]
    public async Task Cards_of_another_board_are_refused()
    {
        var board = await _client.CreateBoardAsync("Mine");
        var other = await _client.CreateBoardAsync("Other");
        var stranger = await _client.CreateCardAsync(other.Id, "Elsewhere");

        var response = await _client.PostAsJsonAsync($"/api/buckets/{board.Buckets[0].Id}/cards", new { cardIds = new[] { stranger.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cardIds", await response.Content.ReadAsStringAsync());
    }

    private async Task<CardDetail> CreateCard(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/cards", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private async Task<PlacedCards> Place(Guid bucketId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/buckets/{bucketId}/cards", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlacedCards>())!;
    }
}
