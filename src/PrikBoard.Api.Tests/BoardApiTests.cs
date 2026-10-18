using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        Assert.Equal([40, 500, 960], board.Buckets.Select(b => b.X));
        Assert.All(board.Buckets, b => Assert.Equal((40, 420, 600), (b.Y, b.Width, b.Height)));
        Assert.Empty(board.Notes);

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
    public async Task Note_requests_are_validated()
    {
        var board = await CreateBoard("Validation");
        var notesUrl = $"/api/boards/{board.Id}/notes";

        var tooLong = await _client.PostAsJsonAsync(notesUrl, new { title = new string('x', 201) });
        var negative = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", x = -20, y = 0 });
        var onlyX = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", x = 100 });
        var both = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", bucketId = board.Buckets[0].Id, x = 0, y = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, onlyX.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task Notes_default_to_free_spots_in_the_first_bucket()
    {
        var board = await CreateBoard("Defaults");
        var todo = board.Buckets[0];

        var first = await CreateNote(board.Id, new { title = "First" });
        var second = await CreateNote(board.Id, new { title = "Second", color = "Pink" });
        var third = await CreateNote(board.Id, new { title = "Third" });

        Assert.All([first, second, third], n => Assert.Equal(todo.Id, n.BucketId));
        Assert.Equal(NoteColor.Yellow, first.Color);
        Assert.Equal(NoteColor.Pink, second.Color);
        Assert.Equal([(60, 80), (240, 80), (60, 240)], new[] { first, second, third }.Select(n => (n.X, n.Y)));
    }

    [Fact]
    public async Task Moving_a_note_to_a_bucket_puts_it_in_a_free_spot_inside()
    {
        var board = await CreateBoard("Moves");
        var (todo, done) = (board.Buckets[0], board.Buckets[2]);
        var first = await CreateNote(board.Id, new { title = "First" });
        await CreateNote(board.Id, new { title = "Second", bucketId = done.Id });

        var moved = await MoveNote(first.Id, new { bucketId = done.Id });

        Assert.Equal(done.Id, moved.BucketId);
        Assert.Equal((1160, 80), (moved.X, moved.Y));

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.DoesNotContain(snapshot!.Notes, n => n.BucketId == todo.Id);
        Assert.Equal(["Second", "First"], snapshot.Notes.Select(n => n.Title));
    }

    [Theory]
    [InlineData(531, 289, 540, 280, "Doing")]
    [InlineData(1370, 700, 1380, 700, null)]
    public async Task Moving_a_note_to_coordinates_snaps_to_the_grid_and_picks_the_bucket_under_its_centre(
        int x, int y, int expectedX, int expectedY, string? expectedBucket)
    {
        var board = await CreateBoard("Coordinates");
        var note = await CreateNote(board.Id, new { title = "Wanderer" });

        var moved = await MoveNote(note.Id, new { x, y });

        Assert.Equal((expectedX, expectedY), (moved.X, moved.Y));
        Assert.Equal(board.Buckets.SingleOrDefault(b => b.Name == expectedBucket)?.Id, moved.BucketId);
    }

    [Fact]
    public async Task Moving_to_a_bucket_on_another_board_is_rejected()
    {
        var board = await CreateBoard("Source");
        var other = await CreateBoard("Other");
        var note = await CreateNote(board.Id, new { title = "Stay here" });

        var response = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/move", new { bucketId = other.Buckets[0].Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Moving_a_bucket_carries_its_notes_along()
    {
        var board = await CreateBoard("Carry");
        var todo = board.Buckets[0];
        var inside = await CreateNote(board.Id, new { title = "Inside" });
        var loose = await CreateNote(board.Id, new { title = "Loose", x = 40, y = 700 });

        var response = await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = 40, y = 800 });
        response.EnsureSuccessStatusCode();

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var movedInside = snapshot!.Notes.Single(n => n.Id == inside.Id);
        var stillLoose = snapshot.Notes.Single(n => n.Id == loose.Id);
        Assert.Equal((60, 840, todo.Id), (movedInside.X, movedInside.Y, movedInside.BucketId));
        Assert.Equal((40, 700), (stillLoose.X, stillLoose.Y));
        Assert.Null(stillLoose.BucketId);
    }

    [Fact]
    public async Task A_new_bucket_adopts_the_loose_notes_it_covers_and_sits_on_top()
    {
        var board = await CreateBoard("Adopt");
        var loose = await CreateNote(board.Id, new { title = "Loose", x = 40, y = 700 });
        var inTodo = await CreateNote(board.Id, new { title = "In to do" });

        var created = await _client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/buckets", new { name = "Review", x = 20, y = 60, width = 300, height = 800 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var review = (await created.Content.ReadFromJsonAsync<BucketDetail>())!;

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal("Review", snapshot!.Buckets[^1].Name);
        Assert.Equal(review.Id, snapshot.Notes.Single(n => n.Id == loose.Id).BucketId);
        Assert.Equal(review.Id, snapshot.Notes.Single(n => n.Id == inTodo.Id).BucketId);
    }

    [Fact]
    public async Task Bucket_geometry_snaps_to_the_grid_with_a_minimum_size()
    {
        var board = await CreateBoard("Snap");

        var response = await _client.PatchAsJsonAsync(
            $"/api/buckets/{board.Buckets[1].Id}", new { name = "In progress", x = 509, y = 31, width = 211, height = 200 });
        var bucket = (await response.Content.ReadFromJsonAsync<BucketDetail>())!;

        Assert.Equal(("In progress", 500, 40, 220, 200), (bucket.Name, bucket.X, bucket.Y, bucket.Width, bucket.Height));
    }

    [Fact]
    public async Task Deleting_a_bucket_leaves_its_notes_loose_in_place()
    {
        var board = await CreateBoard("Unbucket");
        var note = await CreateNote(board.Id, new { title = "Survivor" });

        (await _client.DeleteAsync($"/api/buckets/{board.Buckets[0].Id}")).EnsureSuccessStatusCode();

        var after = await _client.GetFromJsonAsync<NoteDetail>($"/api/notes/{note.Id}");
        Assert.Equal((note.X, note.Y), (after!.X, after.Y));
        Assert.Null(after.BucketId);
    }

    [Fact]
    public async Task Notes_on_a_board_without_buckets_land_loose_in_free_spots()
    {
        var board = await CreateBoard("Empty");
        foreach (var bucket in board.Buckets) (await _client.DeleteAsync($"/api/buckets/{bucket.Id}")).EnsureSuccessStatusCode();

        var first = await CreateNote(board.Id, new { title = "First" });
        var second = await CreateNote(board.Id, new { title = "Second" });

        Assert.Null(first.BucketId);
        Assert.NotEqual((first.X, first.Y), (second.X, second.Y));
    }

    [Fact]
    public async Task Search_finds_notes_by_title_or_body_case_insensitively()
    {
        var board = await CreateBoard("Search");
        await CreateNote(board.Id, new { title = "Design LOGO" });
        await CreateNote(board.Id, new { title = "Write copy", body = "Mention the logo" });
        await CreateNote(board.Id, new { title = "Book venue" });

        var hits = await _client.GetFromJsonAsync<List<NoteDetail>>($"/api/boards/{board.Id}/notes?search=logo");

        Assert.Equal(["Design LOGO", "Write copy"], hits!.Select(n => n.Title));
    }

    [Fact]
    public async Task Deleting_a_board_removes_its_notes()
    {
        var board = await CreateBoard("Temporary");
        var note = await CreateNote(board.Id, new { title = "Gone soon" });

        (await _client.DeleteAsync($"/api/boards/{board.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
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
                "listBoards", "getBoard", "createBucket", "updateBucket", "deleteBucket",
                "createNote", "moveNote", "updateNote", "deleteNote",
                "whoAmI", "listAuthProviders",
            },
            operationIds);
    }

    private async Task<BoardDetail> CreateBoard(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
    }

    private async Task<NoteDetail> CreateNote(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/notes", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }

    private async Task<NoteDetail> MoveNote(Guid noteId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/notes/{noteId}/move", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }
}
