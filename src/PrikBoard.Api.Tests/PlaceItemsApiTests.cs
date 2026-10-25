using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class PlaceItemsApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
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

        // The image sits a grid step below the card; the first goes a grid step below the image (100 high), the second below the first.
        Assert.Equal((60, 60), (there.X, there.Y));
        Assert.Equal([(60, 320), (60, 460)], placed.Cards.Select(n => (n.X, n.Y)));
        Assert.All(placed.Cards, n => Assert.Equal(todo.Id, n.BucketId));
        Assert.Equal((todo.X, todo.Y, todo.Width, todo.Height), (placed.Bucket.X, placed.Bucket.Y, placed.Bucket.Width, placed.Bucket.Height));
    }

    [Fact]
    public async Task With_only_cards_taking_room_cards_go_over_images_and_drawings()
    {
        var board = await _client.CreateBoardAsync("Cards only");
        var todo = board.Buckets[0];
        await CreateCard(board.Id, new { title = "There", bucketId = todo.Id });
        await Upload(board.Id, "?x=60&y=200&width=160");
        await Draw(board.Id, 60, 340);
        var first = await CreateCard(board.Id, new { title = "First", x = 1000, y = 1000 });
        var second = await CreateCard(board.Id, new { title = "Second", x = 1300, y = 1000 });

        var placed = await Place(todo.Id, new { cardIds = new[] { first.Id, second.Id }, onlyCardsTakeRoom = true });

        // A grid step below the card, over the image and the drawing.
        Assert.Equal([(60, 200), (60, 340)], placed.Cards.Select(n => (n.X, n.Y)));
    }

    [Fact]
    public async Task Items_dropped_by_the_pointer_join_the_bucket_named_or_stay_loose()
    {
        var board = await _client.CreateBoardAsync("Pointer");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        var card = await CreateCard(board.Id, new { title = "Card", x = 1000, y = 1000 });
        var image = await Upload(board.Id, "?x=1000&y=1400&width=160");
        var drawing = await Draw(board.Id, 1000, 1800);
        // Over To do's right edge, with the center in Doing.
        var edge = (x: todo.X + todo.Width - 40, y: todo.Y + 60);

        var byCenter = await Ok<CardDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { edge.x, edge.y }));
        var named = await Ok<CardDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { edge.x, edge.y, bucketId = todo.Id }));
        var loose = await Ok<CardDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { x = todo.X + 20, y = todo.Y + 20, loose = true }));
        var created = await CreateCard(board.Id, new { title = "New", x = todo.X + 20, y = todo.Y + 200, loose = true });
        var imageIn = await Ok<ImageDetail>(await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { edge.x, y = todo.Y + 300, bucketId = todo.Id }));
        var imageLoose = await Ok<ImageDetail>(await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { x = todo.X + 20, y = todo.Y + 300, loose = true }));
        var drawingIn = await Ok<DrawingDetail>(await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { edge.x, y = todo.Y + 460, bucketId = todo.Id }));
        var drawingLoose = await Ok<DrawingDetail>(await _client.PatchAsJsonAsync($"/api/drawings/{drawing.Id}", new { x = todo.X + 20, y = todo.Y + 460, loose = true }));
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        var restored = await Ok<CardDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/restore", new { edge.x, edge.y, bucketId = todo.Id }));

        Assert.Equal(doing.Id, byCenter.BucketId);
        Assert.Equal((edge.x, edge.y, todo.Id), (named.X, named.Y, named.BucketId));
        Assert.Null(loose.BucketId);
        Assert.Null(created.BucketId);
        Assert.Equal(todo.Id, imageIn.BucketId);
        Assert.Null(imageLoose.BucketId);
        Assert.Equal(todo.Id, drawingIn.BucketId);
        Assert.Null(drawingLoose.BucketId);
        Assert.Equal(todo.Id, restored.BucketId);
    }

    [Fact]
    public async Task Items_hanging_over_a_buckets_edge_stay_in_it_when_it_moves()
    {
        var board = await _client.CreateBoardAsync("Hanging over");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        var card = await CreateCard(board.Id, new { title = "Card", x = 1000, y = 1000 });
        var image = await Upload(board.Id, "?x=1000&y=1400&width=160");
        // Over To do's right edge, with the centers in Doing.
        var x = todo.X + todo.Width - 40;
        await Ok<CardDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { x, y = todo.Y + 60, bucketId = todo.Id }));
        await Ok<ImageDetail>(await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { x, y = todo.Y + 300, bucketId = todo.Id }));

        // Folded, then moved down along Doing: the items go with it, whatever their centers now lie over.
        await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { collapsed = true }));
        await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { y = doing.Y + 400 }));
        await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { y = todo.Y + 400 }));

        var snapshot = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal((todo.Id, todo.Y + 460), (snapshot.Cards.Single(n => n.Id == card.Id).BucketId, snapshot.Cards.Single(n => n.Id == card.Id).Y));
        Assert.Equal(todo.Id, snapshot.Images.Single(i => i.Id == image.Id).BucketId);
    }

    [Fact]
    public async Task A_drop_names_a_bucket_it_overlaps_or_loose_with_a_place()
    {
        var board = await _client.CreateBoardAsync("Pointer checks");
        var todo = board.Buckets[0];
        var card = await CreateCard(board.Id, new { title = "Card", x = 1000, y = 1000 });
        var image = await Upload(board.Id, "?x=1000&y=1400&width=160");

        var both = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { x = 60, y = 60, bucketId = todo.Id, loose = true });
        var noPlace = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { loose = true });
        var away = await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { x = 3000, y = 3000, bucketId = todo.Id });
        var imageNoPlace = await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { bucketId = todo.Id });

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noPlace.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, away.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, imageNoPlace.StatusCode);
        Assert.Equal(1400, (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Images.Single(i => i.Id == image.Id).Y);
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // To do grows left, up and down; Done, with nothing to its right, grows right.
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
    public async Task A_full_bucket_grows_only_as_far_as_needed_over_its_neighbour()
    {
        var board = await _client.CreateBoardAsync("Over the neighbour");
        var todo = board.Buckets[0];
        for (var i = 0; i < 4; i++) await CreateCard(board.Id, new { title = $"Full {i}", bucketId = todo.Id });
        var extra = await CreateCard(board.Id, new { title = "Extra", x = 1000, y = 1000 });

        var placed = await Place(todo.Id, new { cardIds = new[] { extra.Id }, grow = "Right" });

        // To do grows by one column, over Doing (stacked above it), and the card still joins To do.
        Assert.Equal((40, 460), (placed.Bucket.X, placed.Bucket.Width));
        Assert.Equal((280, 60, todo.Id), (placed.Cards[0].X, placed.Cards[0].Y, placed.Cards[0].BucketId));
        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal(todo.Id, snapshot!.Cards.Single(n => n.Id == extra.Id).BucketId);
    }

    [Fact]
    public async Task Room_left_by_cards_off_the_grid_is_used_and_a_neighbours_cards_are_no_obstacle()
    {
        var board = await _client.CreateBoardAsync("Packed");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        // Off the bucket's card grid: a slot grid from its corner would see both slots below them as taken.
        await CreateCard(board.Id, new { title = "Odd 1", x = 60, y = 100 });
        await CreateCard(board.Id, new { title = "Odd 2", x = 60, y = 260 });
        // Doing's card, right next to To do: in the way only for Doing's own cards.
        await CreateCard(board.Id, new { title = "Doing's", bucketId = doing.Id });
        var extra = await CreateCard(board.Id, new { title = "Extra 1", x = 1000, y = 1000 });
        var more = await CreateCard(board.Id, new { title = "Extra 2", x = 1300, y = 1000 });
        var last = await CreateCard(board.Id, new { title = "Extra 3", x = 1600, y = 1000 });

        var placed = await Place(todo.Id, new { cardIds = new[] { extra.Id, more.Id, last.Id }, grow = "Right" });

        // Below the two (y 260 + 120 + 20 = 400) first, then one column more to the right, over Doing's card.
        Assert.Equal([(60, 400), (280, 60), (280, 200)], placed.Cards.Select(n => (n.X, n.Y)));
        Assert.Equal((40, 460, 600), (placed.Bucket.X, placed.Bucket.Width, placed.Bucket.Height));
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
    public async Task Images_and_drawings_go_into_a_collapsed_bucket_too()
    {
        var board = await _client.CreateBoardAsync("Folded items");
        var done = board.Buckets[2];
        (await _client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        var image = await Upload(board.Id, "?x=1000&y=1000&width=160");
        var drawing = await Draw(board.Id, 1400, 1000);

        var placed = await Place(done.Id, new { imageIds = new[] { image.Id }, drawingIds = new[] { drawing.Id } });

        // Cards first, then images, then drawings: the image takes the top-left room, the drawing the room below it.
        Assert.Empty(placed.Cards);
        Assert.Equal((done.Id, 620, 60), (placed.Images[0].BucketId, placed.Images[0].X, placed.Images[0].Y));
        Assert.Equal(done.Id, placed.Drawings[0].BucketId);
        Assert.True(placed.Drawings[0].Y >= 60 + image.Height + 20, "The drawing goes a grid step below the image.");
        var after = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(done.Id, after.Images.Single(i => i.Id == image.Id).BucketId);
        Assert.Equal(done.Id, after.Drawings.Single(d => d.Id == drawing.Id).BucketId);
    }

    [Fact]
    public async Task Items_are_packed_at_their_own_size()
    {
        var board = await _client.CreateBoardAsync("Sizes");
        var todo = board.Buckets[0];
        var card = await CreateCard(board.Id, new { title = "Card", x = 1000, y = 1000 });
        // Narrower than a card: two fit side by side where no card would.
        var left = await Upload(board.Id, "?x=1000&y=1400&width=80", 80, 50);
        var right = await Upload(board.Id, "?x=1200&y=1400&width=80", 80, 50);

        var placed = await Place(todo.Id, new { cardIds = new[] { card.Id }, imageIds = new[] { left.Id, right.Id } });

        Assert.Equal((60, 60), (placed.Cards[0].X, placed.Cards[0].Y));
        Assert.Equal([(60, 200), (160, 200)], placed.Images.Select(i => (i.X, i.Y)));
        Assert.All(placed.Images, i => Assert.Equal(todo.Id, i.BucketId));
    }

    [Fact]
    public async Task An_image_wider_than_the_bucket_grows_it_sideways()
    {
        var board = await _client.CreateBoardAsync("Wide");
        var done = board.Buckets[2];
        var wide = await Upload(board.Id, "?x=1000&y=1400&width=400", 400, 100);

        var placed = await Place(done.Id, new { imageIds = new[] { wide.Id } });

        Assert.True(placed.Bucket.Width >= 400 + 40, "The bucket grows to the right until the image fits.");
        Assert.Equal((done.X + 20, done.Y + 20), (placed.Images[0].X, placed.Images[0].Y));
    }

    [Fact]
    public async Task Nothing_to_place_and_items_on_a_card_are_refused()
    {
        var board = await _client.CreateBoardAsync("Refused");
        var card = await CreateCard(board.Id, new { title = "Holder" });
        var attached = await Upload(board.Id, $"?cardId={card.Id}");
        var bucket = board.Buckets[0].Id;

        var empty = await _client.PostAsJsonAsync($"/api/buckets/{bucket}/items", new { });
        var onCard = await _client.PostAsJsonAsync($"/api/buckets/{bucket}/items", new { imageIds = new[] { attached.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("cardIds", await empty.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, onCard.StatusCode);
        Assert.Contains("imageIds", await onCard.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cards_of_another_board_are_refused()
    {
        var board = await _client.CreateBoardAsync("Mine");
        var other = await _client.CreateBoardAsync("Other");
        var stranger = await _client.CreateCardAsync(other.Id, "Elsewhere");

        var response = await _client.PostAsJsonAsync($"/api/buckets/{board.Buckets[0].Id}/items", new { cardIds = new[] { stranger.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cardIds", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_loose_card_the_grown_bucket_covers_joins_it_and_its_history_says_so()
    {
        var board = await _client.CreateBoardAsync("Covered");
        var todo = board.Buckets[0];
        for (var i = 0; i < 4; i++) await CreateCard(board.Id, new { title = $"Full {i}", bucketId = todo.Id });
        // Loose, just below the full bucket.
        var below = await CreateCard(board.Id, new { title = "Below", x = todo.X + 20, y = todo.Y + todo.Height });
        var extra = await CreateCard(board.Id, new { title = "Extra", x = 1000, y = 1000 });
        Assert.Null(below.BucketId);

        var placed = await Place(todo.Id, new { cardIds = new[] { extra.Id } });

        // The bucket grows down past the loose card, which stays put and joins it.
        Assert.True(placed.Bucket.Y + placed.Bucket.Height > below.Y + Layout.CardHeight);
        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var now = snapshot!.Cards.Single(n => n.Id == below.Id);
        Assert.Equal((todo.Id, below.X, below.Y), (now.BucketId, now.X, now.Y));
        var history = await _client.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{below.Id}/history");
        Assert.Equal(CardChangeKind.Bucket, Assert.Single(history![0].Entries).Kind);
    }

    [Fact]
    public async Task A_bucket_as_tall_as_it_can_be_grows_sideways_so_no_card_lands_on_another()
    {
        var board = await _client.CreateBoardAsync("Five hundred");
        var todo = board.Buckets[0];
        var cards = new List<CardDetail>();
        for (var batch = 0; batch < 10; batch++)
        {
            var requests = Enumerable.Range(0, 50).Select(i => new { title = $"Card {batch * 50 + i}", x = 5000 + i * 220, y = 5000 + batch * 140 });
            var response = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/cards/batch", new { cards = requests });
            cards.AddRange((await response.Content.ReadFromJsonAsync<List<CardDetail>>())!);
        }

        var placed = await Place(todo.Id, new { cardIds = cards.Select(n => n.Id) });

        // One column holds 71 cards at the most height, so it takes eight columns.
        Assert.Equal((Layout.MaxBucketSize - 20) / 140 * 140 + 20, placed.Bucket.Height);
        Assert.Equal(20 + 8 * 220, placed.Bucket.Width);
        Assert.Equal(500, placed.Cards.Select(n => (n.X, n.Y)).Distinct().Count());
        Assert.All(placed.Cards, n =>
        {
            Assert.InRange(n.X, placed.Bucket.X, placed.Bucket.X + placed.Bucket.Width - Layout.CardWidth);
            Assert.InRange(n.Y, placed.Bucket.Y, placed.Bucket.Y + placed.Bucket.Height - Layout.CardHeight);
        });
    }

    private async Task<CardDetail> CreateCard(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/cards", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private async Task<PlacedItems> Place(Guid bucketId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/buckets/{bucketId}/items", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlacedItems>())!;
    }

    private async Task<ImageDetail> Upload(Guid boardId, string query, int width = 160, int height = 100)
    {
        var response = await _client.PostAsync($"/api/boards/{boardId}/images{query}",
            new ByteArrayContent(TestImages.Png(width, height)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }

    private async Task<DrawingDetail> Draw(Guid boardId, int x, int y)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/drawings", new { x, y, points = Squiggle });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DrawingDetail>())!;
    }

    private static readonly double[][] Squiggle = [[2, 2, 0.5], [40, 10, 0.5], [100, 30, 1]];
}
