using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class BoardApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task New_board_starts_with_default_buckets_side_by_side_and_is_listed()
    {
        var board = await CreateBoard("Launch");

        Assert.Equal(["To do", "Doing", "Done"], board.Buckets.Select(b => b.Name));
        Assert.Equal([40, 320, 600], board.Buckets.Select(b => b.X));
        Assert.All(board.Buckets, b => Assert.Equal((40, 240, 600), (b.Y, b.Width, b.Height)));
        Assert.Empty(board.Cards);

        var boards = await _client.GetFromJsonAsync<List<BoardSummary>>("/api/boards");
        Assert.Contains(boards!, b => b.Id == board.Id && b.Name == "Launch");
    }

    [Fact]
    public async Task Board_name_is_required()
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Card_requests_are_validated()
    {
        var board = await CreateBoard("Validation");
        var cardsUrl = $"/api/boards/{board.Id}/cards";

        var tooLong = await _client.PostAsJsonAsync(cardsUrl, new { title = new string('x', 201) });
        // The board has no edge at the origin, but it does end somewhere.
        var negative = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", x = -100_020, y = 0 });
        var onlyX = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", x = 100 });
        var outsideBucket = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", bucketId = board.Buckets[0].Id, x = -1000, y = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, onlyX.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, outsideBucket.StatusCode);
    }

    [Fact]
    public async Task A_card_title_may_be_empty_and_is_trimmed()
    {
        var board = await CreateBoard("Untitled");

        var untitled = await CreateCard(board.Id, new { title = "" });
        var padded = await CreateCard(board.Id, new { title = "  Padded  " });
        Assert.Equal("", untitled.Title);
        Assert.Equal("Padded", padded.Title);

        var named = await UpdateCard(untitled.Id, new { title = " Named " });
        var unchanged = await UpdateCard(untitled.Id, new { color = "Sky" });
        var cleared = await UpdateCard(untitled.Id, new { title = "   " });
        Assert.Equal("Named", named.Title);
        Assert.Equal("Named", unchanged.Title);
        Assert.Equal("", cleared.Title);
    }

    [Fact]
    public async Task Cards_default_to_free_spots_in_the_first_bucket()
    {
        var board = await CreateBoard("Defaults");
        var todo = board.Buckets[0];

        var first = await CreateCard(board.Id, new { title = "First" });
        var second = await CreateCard(board.Id, new { title = "Second", color = "Coral" });
        var third = await CreateCard(board.Id, new { title = "Third" });

        Assert.All([first, second, third], n => Assert.Equal(todo.Id, n.BucketId));
        Assert.Equal(CardColor.Butter, first.Color);
        Assert.Equal(CardColor.Coral, second.Color);
        Assert.Equal([(60, 60), (60, 200), (60, 340)], new[] { first, second, third }.Select(n => (n.X, n.Y)));
    }

    [Fact]
    public async Task Moving_a_card_to_a_bucket_puts_it_in_a_free_spot_inside()
    {
        var board = await CreateBoard("Moves");
        var (todo, done) = (board.Buckets[0], board.Buckets[2]);
        var first = await CreateCard(board.Id, new { title = "First" });
        await CreateCard(board.Id, new { title = "Second", bucketId = done.Id });

        var moved = await MoveCard(first.Id, new { bucketId = done.Id });

        Assert.Equal(done.Id, moved.BucketId);
        Assert.Equal((620, 200), (moved.X, moved.Y));

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.DoesNotContain(snapshot!.Cards, n => n.BucketId == todo.Id);
        Assert.Equal(["Second", "First"], snapshot.Cards.Select(n => n.Title));
    }

    [Theory]
    [InlineData(331, 289, 340, 280, "Doing")]
    [InlineData(1370, 700, 1380, 700, null)]
    public async Task Moving_a_card_to_coordinates_snaps_to_the_grid_and_picks_the_bucket_under_its_centre(
        int x, int y, int expectedX, int expectedY, string? expectedBucket)
    {
        var board = await CreateBoard("Coordinates");
        var card = await CreateCard(board.Id, new { title = "Wanderer" });

        var moved = await MoveCard(card.Id, new { x, y });

        Assert.Equal((expectedX, expectedY), (moved.X, moved.Y));
        Assert.Equal(board.Buckets.SingleOrDefault(b => b.Name == expectedBucket)?.Id, moved.BucketId);
    }

    [Fact]
    public async Task Moving_to_a_bucket_on_another_board_is_rejected()
    {
        var board = await CreateBoard("Source");
        var other = await CreateBoard("Other");
        var card = await CreateCard(board.Id, new { title = "Stay here" });

        var response = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { bucketId = other.Buckets[0].Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Moving_a_bucket_carries_its_cards_along()
    {
        var board = await CreateBoard("Carry");
        var todo = board.Buckets[0];
        var inside = await CreateCard(board.Id, new { title = "Inside" });
        var loose = await CreateCard(board.Id, new { title = "Loose", x = 40, y = 700 });

        var response = await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = 40, y = 800 });
        response.EnsureSuccessStatusCode();

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var movedInside = snapshot!.Cards.Single(n => n.Id == inside.Id);
        var stillLoose = snapshot.Cards.Single(n => n.Id == loose.Id);
        Assert.Equal((60, 820, todo.Id), (movedInside.X, movedInside.Y, movedInside.BucketId));
        Assert.Equal((40, 700), (stillLoose.X, stillLoose.Y));
        Assert.Null(stillLoose.BucketId);
    }

    [Fact]
    public async Task Moving_a_bucket_onto_another_keeps_both_their_cards()
    {
        var board = await CreateBoard("Overlap");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        var inTodo = await CreateCard(board.Id, new { title = "To do's", bucketId = todo.Id });
        var inDoing = await CreateCard(board.Id, new { title = "Doing's", bucketId = doing.Id });

        // Doing (on top) lands on To do, then To do (underneath) moves away from under it: each takes only its own card.
        (await _client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { x = todo.X, y = todo.Y })).EnsureSuccessStatusCode();
        var stacked = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = 1200, y = todo.Y })).EnsureSuccessStatusCode();
        var apart = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");

        Assert.Equal(todo.Id, stacked!.Cards.Single(n => n.Id == inTodo.Id).BucketId);
        Assert.Equal(doing.Id, stacked.Cards.Single(n => n.Id == inDoing.Id).BucketId);
        var (todoCard, doingCard) = (apart!.Cards.Single(n => n.Id == inTodo.Id), apart.Cards.Single(n => n.Id == inDoing.Id));
        Assert.Equal((inTodo.X + 1200 - todo.X, inTodo.Y, todo.Id), (todoCard.X, todoCard.Y, todoCard.BucketId));
        Assert.Equal((inDoing.X + todo.X - doing.X, inDoing.Y, doing.Id), (doingCard.X, doingCard.Y, doingCard.BucketId));
    }

    [Fact]
    public async Task A_collapsed_bucket_over_another_only_takes_the_room_of_its_square()
    {
        var board = await CreateBoard("Folded over");
        var (doing, done) = (board.Buckets[1], board.Buckets[2]);
        await CreateCard(board.Id, new { title = "First", bucketId = doing.Id });
        await CreateCard(board.Id, new { title = "Done's", bucketId = done.Id });
        // Done (stacked on top) moves over Doing, below Doing's first row, and folds into a square over Doing's second slot.
        (await _client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { x = doing.X, y = doing.Y + 140 })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { collapsed = true })).EnsureSuccessStatusCode();

        var next = await CreateCard(board.Id, new { title = "Next", bucketId = doing.Id });
        var after = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Buckets.Single(b => b.Id == doing.Id);

        Assert.Equal((doing.X + 20, doing.Y + 300, doing.Id), (next.X, next.Y, next.BucketId));
        Assert.Equal(doing.Height, after.Height);
    }

    [Fact]
    public async Task A_card_dropped_where_buckets_overlap_joins_the_bucket_named_with_its_position()
    {
        var board = await CreateBoard("Drop in front");
        var (todo, doing, done) = (board.Buckets[0], board.Buckets[1], board.Buckets[2]);
        // Doing (stacked on top) moves onto To do, which a screen may still show in front.
        (await _client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { x = todo.X, y = todo.Y })).EnsureSuccessStatusCode();
        var (x, y) = (todo.X + 20, todo.Y + 20);
        var card = await CreateCard(board.Id, new { title = "Dropped", x = 0, y = 0 });

        var byPosition = await MoveCard(card.Id, new { x, y });
        var named = await MoveCard(card.Id, new { x, y, bucketId = todo.Id });
        var created = await CreateCard(board.Id, new { title = "New", x, y, bucketId = todo.Id });
        var elsewhere = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { x, y, bucketId = done.Id });
        // Named with the position, a collapsed bucket takes the card too, as with bucketId alone; redoing a drop on one needs that.
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        await MoveCard(card.Id, new { x, y });
        var collapsed = await MoveCard(card.Id, new { x, y, bucketId = todo.Id });

        Assert.Equal(doing.Id, byPosition.BucketId);
        Assert.Equal((x, y, todo.Id), (named.X, named.Y, named.BucketId));
        Assert.Equal((x, y, todo.Id), (created.X, created.Y, created.BucketId));
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Contains("bucketId", (await elsewhere.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        Assert.Equal(todo.Id, collapsed.BucketId);
    }

    [Fact]
    public async Task A_resized_bucket_lets_go_of_its_cards_outside_it_and_takes_none()
    {
        var board = await CreateBoard("Resize members");
        var (doing, done) = (board.Buckets[1], board.Buckets[2]);
        var first = await CreateCard(board.Id, new { title = "First", bucketId = done.Id });
        var second = await CreateCard(board.Id, new { title = "Second", bucketId = done.Id });
        var inDoing = await CreateCard(board.Id, new { title = "Doing's", bucketId = doing.Id });
        var loose = await CreateCard(board.Id, new { title = "Loose", x = done.X + 20, y = done.Y + done.Height + 100 });

        // Done (on top) stretches left over Doing's card and gets so short that its second card falls out.
        var resized = await _client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { x = doing.X - 20, width = done.X + done.Width - doing.X + 20, height = 160 });
        resized.EnsureSuccessStatusCode();
        var shrunk = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Cards;
        // Then tall again: the loose cards it now covers stay loose (only a bucket drawn around them takes them).
        (await _client.PatchAsJsonAsync($"/api/buckets/{done.Id}", new { height = done.Height + 300 })).EnsureSuccessStatusCode();
        var grown = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Cards;

        Assert.Equal(done.Id, shrunk.Single(n => n.Id == first.Id).BucketId);
        Assert.Null(shrunk.Single(n => n.Id == second.Id).BucketId);
        Assert.Equal(doing.Id, shrunk.Single(n => n.Id == inDoing.Id).BucketId);
        Assert.Null(grown.Single(n => n.Id == second.Id).BucketId);
        Assert.Null(grown.Single(n => n.Id == loose.Id).BucketId);
        Assert.Equal(doing.Id, grown.Single(n => n.Id == inDoing.Id).BucketId);
    }

    [Fact]
    public async Task A_bucket_that_grows_for_a_new_card_leaves_the_loose_card_below_it_loose()
    {
        var board = await CreateBoard("Grow over loose");
        var done = board.Buckets[2];
        var loose = await CreateCard(board.Id, new { title = "Below", x = done.X + 20, y = done.Y + done.Height + 40 });
        Assert.Null(loose.BucketId);

        // Fill Done until it grows down over the loose card, which stays loose and where it is.
        for (var i = 0; i < 12; i++) await CreateCard(board.Id, new { title = $"Filler {i}", bucketId = done.Id });

        var after = (await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{loose.Id}"))!;
        Assert.Null(after.BucketId);
        Assert.Equal((loose.X, loose.Y, "Below"), (after.X, after.Y, after.Title));
    }

    [Fact]
    public async Task A_full_bucket_grows_past_a_bucket_stacked_above_it()
    {
        var board = await CreateBoard("Stacked");
        var todo = board.Buckets[0];
        // Created later, so it is stacked above To do, right where To do would grow.
        var below = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/buckets",
            new { name = "Parking", x = todo.X, y = todo.Y + todo.Height, width = todo.Width, height = 300 });
        below.EnsureSuccessStatusCode();

        CardDetail last = null!;
        for (var i = 0; i < 5; i++) last = await CreateCard(board.Id, new { title = $"Card {i}", bucketId = todo.Id });

        // To do holds four; the fifth goes below the parking bucket instead of hidden under it, and still joins To do.
        Assert.Equal(todo.Id, last.BucketId);
        Assert.True(last.Y >= todo.Y + todo.Height + 300, $"Expected the card below the parking bucket, got y {last.Y}.");
    }

    [Fact]
    public async Task The_board_goes_on_left_of_and_above_the_origin()
    {
        var board = await CreateBoard("No edge");
        var todo = board.Buckets[0];
        var inside = await CreateCard(board.Id, new { title = "Inside" });
        var far = await CreateCard(board.Id, new { title = "Far out", x = -600, y = -400 });

        var response = await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = -1000, y = -800 });
        response.EnsureSuccessStatusCode();
        var moved = await MoveCard(far.Id, new { x = -1960, y = -1500 });

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var carried = snapshot!.Cards.Single(n => n.Id == inside.Id);
        Assert.Equal((-600, -400), (far.X, far.Y));
        Assert.Equal((-1960, -1500), (moved.X, moved.Y));
        Assert.Equal((-980, -780, todo.Id), (carried.X, carried.Y, carried.BucketId));
    }

    [Fact]
    public async Task Resizing_a_bucket_from_its_top_left_corner_leaves_its_cards_in_place()
    {
        var board = await CreateBoard("Corner");
        var created = await _client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/buckets", new { name = "Corner bucket", x = 200, y = 1000, width = 300, height = 400 });
        var bucket = (await created.Content.ReadFromJsonAsync<BucketDetail>())!;
        var inside = await CreateCard(board.Id, new { title = "Inside", x = 240, y = 1040 });

        var response = await _client.PatchAsJsonAsync($"/api/buckets/{bucket.Id}", new { x = 100, y = 900, width = 400, height = 500 });
        response.EnsureSuccessStatusCode();

        var resized = (await response.Content.ReadFromJsonAsync<BucketDetail>())!;
        Assert.Equal((100, 900, 400, 500), (resized.X, resized.Y, resized.Width, resized.Height));
        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var card = snapshot!.Cards.Single(n => n.Id == inside.Id);
        Assert.Equal((240, 1040, bucket.Id), (card.X, card.Y, card.BucketId));
    }

    [Fact]
    public async Task A_new_bucket_adopts_only_the_loose_cards_it_covers_and_sits_on_top()
    {
        var board = await CreateBoard("Adopt");
        var todo = board.Buckets[0];
        var loose = await CreateCard(board.Id, new { title = "Loose", x = 40, y = 700 });
        var inTodo = await CreateCard(board.Id, new { title = "In to do" });
        // In To do, hanging out of it over where the new bucket goes.
        var hanging = await CreateCard(board.Id, new { title = "Hanging out", x = todo.X + 100, y = todo.Y + todo.Height - 40, bucketId = todo.Id });

        var created = await _client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/buckets", new { name = "Review", x = 20, y = 60, width = 300, height = 800 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var review = (await created.Content.ReadFromJsonAsync<BucketDetail>())!;

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal("Review", snapshot!.Buckets[^1].Name);
        Assert.Equal(review.Id, snapshot.Cards.Single(n => n.Id == loose.Id).BucketId);
        Assert.Equal(todo.Id, snapshot.Cards.Single(n => n.Id == inTodo.Id).BucketId);
        Assert.Equal(todo.Id, snapshot.Cards.Single(n => n.Id == hanging.Id).BucketId);
    }

    [Fact]
    public async Task Items_a_bucket_lets_go_of_go_loose_not_into_another_bucket()
    {
        var board = await CreateBoard("Let go");
        var (todo, doing) = (board.Buckets[0], board.Buckets[1]);
        var card = await CreateCard(board.Id, new { title = "Card", bucketId = todo.Id });
        // Doing moves under To do (To do stays on top), so the card also lies over Doing.
        (await _client.PatchAsJsonAsync($"/api/buckets/{doing.Id}", new { x = todo.X, y = todo.Y })).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/buckets/{todo.Id}/front", null)).EnsureSuccessStatusCode();

        (await _client.DeleteAsync($"/api/buckets/{todo.Id}")).EnsureSuccessStatusCode();
        var afterDelete = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Cards.Single(n => n.Id == card.Id);
        // A restored bucket takes nothing back by itself; the web app's undo puts its items back by id.
        (await _client.PostAsync($"/api/buckets/{todo.Id}/restore", null)).EnsureSuccessStatusCode();
        var afterRestore = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Cards.Single(n => n.Id == card.Id);
        var putBack = await MoveCard(card.Id, new { x = card.X, y = card.Y, bucketId = todo.Id });

        Assert.Null(afterDelete.BucketId);
        Assert.Null(afterRestore.BucketId);
        Assert.Equal(todo.Id, putBack.BucketId);
    }

    [Fact]
    public async Task Resizing_or_folding_a_bucket_takes_no_items()
    {
        var board = await CreateBoard("No taking");
        var todo = board.Buckets[0];
        var loose = await CreateCard(board.Id, new { title = "Loose", x = todo.X + 20, y = todo.Y + todo.Height + 40 });

        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { height = todo.Height + 400 })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { collapsed = false })).EnsureSuccessStatusCode();

        var after = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Cards.Single(n => n.Id == loose.Id);
        Assert.Null(after.BucketId);
    }

    [Fact]
    public async Task Bucket_geometry_snaps_to_the_grid_with_a_minimum_size()
    {
        var board = await CreateBoard("Snap");

        var response = await _client.PatchAsJsonAsync(
            $"/api/buckets/{board.Buckets[1].Id}", new { name = "In progress", x = 509, y = 31, width = 249, height = 171 });
        var bucket = (await response.Content.ReadFromJsonAsync<BucketDetail>())!;
        var tooNarrow = await _client.PatchAsJsonAsync($"/api/buckets/{board.Buckets[1].Id}", new { width = 200 });

        Assert.Equal(("In progress", 500, 40, 240, 180), (bucket.Name, bucket.X, bucket.Y, bucket.Width, bucket.Height));
        Assert.Equal(HttpStatusCode.BadRequest, tooNarrow.StatusCode);
    }

    [Fact]
    public async Task A_full_bucket_grows_to_make_room()
    {
        var board = await CreateBoard("Grow");
        var todo = board.Buckets[0];

        CardDetail last = null!;
        for (var i = 1; i <= 5; i++) last = await CreateCard(board.Id, new { title = $"Task {i}" });

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal((60, 620, todo.Id), (last.X, last.Y, last.BucketId));
        Assert.Equal(720, snapshot!.Buckets[0].Height);
        Assert.Equal(5, snapshot.Cards.Count(n => n.BucketId == todo.Id));
    }

    [Fact]
    public async Task Deleting_a_bucket_leaves_its_cards_loose_in_place()
    {
        var board = await CreateBoard("Unbucket");
        var card = await CreateCard(board.Id, new { title = "Survivor" });

        (await _client.DeleteAsync($"/api/buckets/{board.Buckets[0].Id}")).EnsureSuccessStatusCode();

        var after = await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}");
        Assert.Equal((card.X, card.Y), (after!.X, after.Y));
        Assert.Null(after.BucketId);
    }

    [Fact]
    public async Task Cards_on_a_board_without_buckets_land_loose_in_free_spots()
    {
        var board = await CreateBoard("Empty");
        foreach (var bucket in board.Buckets) (await _client.DeleteAsync($"/api/buckets/{bucket.Id}")).EnsureSuccessStatusCode();

        var first = await CreateCard(board.Id, new { title = "First" });
        var second = await CreateCard(board.Id, new { title = "Second" });

        Assert.Null(first.BucketId);
        Assert.NotEqual((first.X, first.Y), (second.X, second.Y));
    }

    [Fact]
    public async Task Search_finds_cards_by_title_or_content_case_insensitively()
    {
        var board = await CreateBoard("Search");
        await CreateCard(board.Id, new { title = "Design LOGO" });
        await CreateCard(board.Id, new { title = "Write copy", components = new[] { new { kind = "Text", text = "Mention the logo" } } });
        await CreateCard(board.Id, new { title = "Print", components = new[] { new { kind = "Checklist", items = new[] { new { text = "Logo on the cover" } } } } });
        await CreateCard(board.Id, new { title = "Brand", components = new[] { new { kind = "Links", links = new[] { new { title = "Logo files", url = "https://example.com" } } } } });
        await CreateCard(board.Id, new { title = "Book venue" });

        var hits = await _client.GetFromJsonAsync<List<CardDetail>>($"/api/boards/{board.Id}/cards?search=logo");

        Assert.Equal(["Design LOGO", "Write copy", "Print", "Brand"], hits!.Select(n => n.Title));
    }

    [Fact]
    public async Task Search_finds_a_card_by_its_number_or_readable_id()
    {
        var board = await CreateBoard("Numbers");
        var tiles = await CreateCard(board.Id, new { title = "Order 12 tiles" });
        var call = await CreateCard(board.Id, new { title = "Call back" });
        await CreateCard(board.Id, new { title = "Book venue" });
        async Task<List<string>> Search(string search) =>
            (await _client.GetFromJsonAsync<List<CardDetail>>($"/api/boards/{board.Id}/cards?search={Uri.EscapeDataString(search)}"))!.Select(n => n.Title).ToList();

        Assert.Equal((1, 2), (tiles.Number, call.Number));
        // The card with that number first, then the cards with that text.
        Assert.Equal(["Call back", "Order 12 tiles"], await Search("2"));
        Assert.Equal(["Call back"], await Search(" 002 "));
        Assert.Equal(["Call back"], await Search("#2"));
        Assert.Equal(["Call back"], await Search(call.ReadableId.ToLowerInvariant()));
        Assert.Empty(await Search("#9"));
        Assert.Equal(["Order 12 tiles"], await Search("12"));
    }

    [Fact]
    public async Task Cards_carry_tags_assignees_and_content_components()
    {
        var board = await CreateBoard("Details");
        var me = await _client.MeAsync();
        var created = await CreateCard(board.Id, new
        {
            title = "Retry policy for lidar tile ingest",
            tags = new[] { " Backend ", "ingest", "backend", "Tile Index" },
            assigneeIds = new[] { me.Id, me.Id },
            components = new object[]
            {
                new { kind = "Checklist", title = "Acceptance criteria", items = new object[] { new { text = "Backoff with jitter", done = true }, new { text = "Exhausted tiles go to the DLQ" } } },
                new { kind = "Links", links = new[] { new { title = "PR #412", url = "https://example.com/pulls/412" } } },
            },
        });

        Assert.Equal(["backend", "ingest", "tile-index"], created.Tags);
        Assert.Equal([new Assignee(me.Id, me.DisplayName, AssigneeKind.Person, null)], created.Assignees);
        var (checklist, links) = (created.Components[0], created.Components[1]);
        Assert.Equal((CardComponentKind.Checklist, "Acceptance criteria", null), (checklist.Kind, checklist.Title, checklist.Text));
        Assert.Equal([new ChecklistItem("Backoff with jitter", true), new ChecklistItem("Exhausted tiles go to the DLQ")], checklist.Items!);
        Assert.Null(checklist.Links);
        Assert.Equal([new CardLink("PR #412", "https://example.com/pulls/412")], links.Links!);
        Assert.False(created.AgentWorking);

        var response = await _client.PatchAsJsonAsync($"/api/cards/{created.Id}/components/{checklist.Id}", new
        {
            items = new object[] { new { text = "Backoff with jitter", done = true }, new { text = "Exhausted tiles go to the DLQ", done = true } },
        });
        response.EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/cards/{created.Id}", new { agentWorking = true })).EnsureSuccessStatusCode();
        var updated = (await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{created.Id}"))!;

        Assert.All(updated.Components[0].Items!, item => Assert.True(item.Done));
        Assert.True(updated.AgentWorking);
        Assert.Equal(created.Tags, updated.Tags);
        Assert.Equal(created.Assignees, updated.Assignees);
        Assert.Equal((links.Id, links.X, links.Y), (updated.Components[1].Id, updated.Components[1].X, updated.Components[1].Y));
        Assert.Equal(links.Links!, updated.Components[1].Links!);
    }

    [Fact]
    public async Task Cards_link_other_cards_by_their_path()
    {
        var board = await CreateBoard("Card links");
        var target = await CreateCard(board.Id, new { title = "Retry policy" });
        Assert.Equal($"/#/boards/{board.ShortId}/cards/{target.Number}", target.Path);

        var linking = await CreateCard(board.Id, new { title = "Follow-up", components = new[] { LinksComponent("Retry policy", target.Path) } });
        Assert.Equal([new CardLink("Retry policy", target.Path)], linking.Components.Single().Links!);

        var cardsUrl = $"/api/boards/{board.Id}/cards";
        string[] invalid =
        [
            "/somewhere", $"/#/boards/{board.Id}/cards/abc", $"/#/boards/{board.ShortId}/cards/abc", $"/#/boards/{board.ShortId}/cards/007",
            $"/#/boards/{board.ShortId}/cards/{target.Id}", $"/#/boards/{board.Id}/cards/{target.Number}",
        ];
        foreach (var url in invalid)
        {
            var response = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", components = new[] { LinksComponent("x", url) } });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, url);
        }
    }

    [Fact]
    public async Task Card_details_are_validated()
    {
        var board = await CreateBoard("Detail validation");
        var cardsUrl = $"/api/boards/{board.Id}/cards";

        var scriptLink = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", components = new[] { LinksComponent("x", "javascript:alert(1)") } });
        var longTag = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", tags = new[] { new string('t', 31) } });
        var stranger = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", assigneeIds = new[] { Guid.NewGuid() } });
        var tooManyPeople = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", assigneeIds = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()) });
        var tooManyTags = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", tags = Enumerable.Range(0, 11).Select(i => $"t{i}") });
        var misfit = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", components = new object[] { new { kind = "Text" }, new { kind = "Text", items = new[] { new { text = "x" } } } } });
        var longItem = await _client.PostAsJsonAsync(cardsUrl, new { title = "Ok", components = new[] { new { kind = "Checklist", items = new[] { new { text = new string('x', 301) } } } } });

        Assert.Equal(HttpStatusCode.BadRequest, scriptLink.StatusCode);
        Assert.Contains("components[0].links", await scriptLink.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, longTag.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.Contains("assigneeIds", await stranger.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, tooManyPeople.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyTags.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, misfit.StatusCode);
        Assert.Contains("components[1].items", await misfit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, longItem.StatusCode);
    }

    [Fact]
    public async Task Cards_can_be_filtered_by_tags_and_assignee()
    {
        var board = await CreateBoard("Filters");
        var mila = await factory.CreateSignedInClientAsync("mila-filters@example.com");
        await mila.RenameAsync("Mila V.");
        await _client.ShareAsync(board.Id, mila, BoardRole.Editor);
        var milaId = (await mila.MeAsync()).Id;
        var meId = (await _client.MeAsync()).Id;
        await CreateCard(board.Id, new { title = "Ingest", tags = new[] { "backend" }, assigneeIds = new[] { meId } });
        await CreateCard(board.Id, new { title = "Dashboard", tags = new[] { "observability" }, assigneeIds = new[] { milaId } });
        await CreateCard(board.Id, new { title = "DLQ", tags = new[] { "backend" }, assigneeIds = new[] { milaId, meId } });
        await CreateCard(board.Id, new { title = "Docs", tags = new[] { "docs" } });

        var url = $"/api/boards/{board.Id}/cards";
        var backend = await _client.GetFromJsonAsync<List<CardDetail>>($"{url}?tag=Backend");
        var either = await _client.GetFromJsonAsync<List<CardDetail>>($"{url}?tag=backend&tag=observability");
        var byName = await _client.GetFromJsonAsync<List<CardDetail>>($"{url}?assignee=mila%20v.");
        var byId = await _client.GetFromJsonAsync<List<CardDetail>>($"{url}?assignee={milaId}");

        Assert.Equal(["Ingest", "DLQ"], backend!.Select(n => n.Title));
        Assert.Equal(["Ingest", "Dashboard", "DLQ"], either!.Select(n => n.Title));
        Assert.Equal(["Dashboard", "DLQ"], byName!.Select(n => n.Title));
        Assert.Equal(["Dashboard", "DLQ"], byId!.Select(n => n.Title));
    }

    [Fact]
    public async Task Deleting_a_board_removes_its_cards()
    {
        var board = await CreateBoard("Temporary");
        var card = await CreateCard(board.Id, new { title = "Gone soon" });

        (await _client.DeleteAsync($"/api/boards/{board.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cards/{card.Id}")).StatusCode);
    }

    [Fact]
    public async Task Without_sign_in_providers_the_api_reference_is_still_readable()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/scalar/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_exposes_named_operations_for_ai_clients()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));

        var operationIds = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(op => op.Value.TryGetProperty("operationId", out _))
            .Select(op => op.Value.GetProperty("operationId").GetString())
            .ToHashSet();

        Assert.Superset(
            new HashSet<string?>
            {
                "listBoards", "getBoard", "createBucket", "updateBucket", "bringBucketToFront", "deleteBucket",
                "createCard", "getCardByNumber", "moveCard", "updateCard", "deleteCard", "restoreCard", "purgeCard", "emptyTrash", "setCardWorking", "restoreBucket", "listCardHistory",
                "addCardComponent", "updateCardComponent", "deleteCardComponent",
                "listBoardTags", "createBoardTag", "updateBoardTag", "deleteBoardTag", "restoreBoardTag",
                "whoAmI", "listAuthProviders", "getVersion",
                "uploadImage", "getImageContent", "updateImage", "deleteImage", "restoreImage",
                "createDrawing", "getDrawingContent", "addDrawingStroke", "deleteDrawingStroke", "transformDrawing", "setDrawingStrokes", "updateDrawing", "deleteDrawing", "restoreDrawing",
                "adminListBoards", "adminDeleteBoard", "adminListUsers", "adminSetBoardMember", "adminRemoveBoardMember",
                "adminUpdateUser", "adminDeleteUser",
                "getDiscordBot", "setDiscordBot", "deleteDiscordBot", "leaveDiscordServer", "getDiscordAccount", "previewDiscordAccountLink", "linkDiscordAccount", "unlinkDiscordAccount",
                "listDiscordConnections", "listDiscordServers", "connectDiscordChannel", "updateDiscordChannel", "removeDiscordConnection", "removeDiscordWholeServer", "removeDiscordChannel",
                "getSetupStatus", "unlockSetup", "listSignInProviders", "createSignInProvider", "updateSignInProvider",
                "deleteSignInProvider", "checkOpenIdAuthority", "beginGitHubApp", "getSystemChecks", "getRecoveryKit",
            },
            operationIds);
    }

    private async Task<BoardDetail> CreateBoard(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
    }

    private async Task<CardDetail> CreateCard(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/cards", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private async Task<CardDetail> UpdateCard(Guid cardId, object request)
    {
        var response = await _client.PatchAsJsonAsync($"/api/cards/{cardId}", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private static object LinksComponent(string title, string url) => new { kind = "Links", links = new[] { new { title, url } } };

    private async Task<CardDetail> MoveCard(Guid cardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/cards/{cardId}/move", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }
}
