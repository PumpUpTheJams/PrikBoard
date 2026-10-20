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
        Assert.Equal([40, 320, 600], board.Buckets.Select(b => b.X));
        Assert.All(board.Buckets, b => Assert.Equal((40, 240, 600), (b.Y, b.Width, b.Height)));
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
        // The board has no edge at the origin, but it does end somewhere.
        var negative = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", x = -100_020, y = 0 });
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
        var second = await CreateNote(board.Id, new { title = "Second", color = "Coral" });
        var third = await CreateNote(board.Id, new { title = "Third" });

        Assert.All([first, second, third], n => Assert.Equal(todo.Id, n.BucketId));
        Assert.Equal(NoteColor.Butter, first.Color);
        Assert.Equal(NoteColor.Coral, second.Color);
        Assert.Equal([(60, 60), (60, 200), (60, 340)], new[] { first, second, third }.Select(n => (n.X, n.Y)));
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
        Assert.Equal((620, 200), (moved.X, moved.Y));

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.DoesNotContain(snapshot!.Notes, n => n.BucketId == todo.Id);
        Assert.Equal(["Second", "First"], snapshot.Notes.Select(n => n.Title));
    }

    [Theory]
    [InlineData(331, 289, 340, 280, "Doing")]
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
        Assert.Equal((60, 820, todo.Id), (movedInside.X, movedInside.Y, movedInside.BucketId));
        Assert.Equal((40, 700), (stillLoose.X, stillLoose.Y));
        Assert.Null(stillLoose.BucketId);
    }

    [Fact]
    public async Task The_board_goes_on_left_of_and_above_the_origin()
    {
        var board = await CreateBoard("No edge");
        var todo = board.Buckets[0];
        var inside = await CreateNote(board.Id, new { title = "Inside" });
        var far = await CreateNote(board.Id, new { title = "Far out", x = -600, y = -400 });

        var response = await _client.PatchAsJsonAsync($"/api/buckets/{todo.Id}", new { x = -1000, y = -800 });
        response.EnsureSuccessStatusCode();
        var moved = await MoveNote(far.Id, new { x = -1960, y = -1500 });

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var carried = snapshot!.Notes.Single(n => n.Id == inside.Id);
        Assert.Equal((-600, -400), (far.X, far.Y));
        Assert.Equal((-1960, -1500), (moved.X, moved.Y));
        Assert.Equal((-980, -780, todo.Id), (carried.X, carried.Y, carried.BucketId));
    }

    [Fact]
    public async Task Resizing_a_bucket_from_its_top_left_corner_leaves_its_notes_in_place()
    {
        var board = await CreateBoard("Corner");
        var created = await _client.PostAsJsonAsync(
            $"/api/boards/{board.Id}/buckets", new { name = "Frame", x = 200, y = 1000, width = 300, height = 400 });
        var frame = (await created.Content.ReadFromJsonAsync<BucketDetail>())!;
        var inside = await CreateNote(board.Id, new { title = "Inside", x = 240, y = 1040 });

        var response = await _client.PatchAsJsonAsync($"/api/buckets/{frame.Id}", new { x = 100, y = 900, width = 400, height = 500 });
        response.EnsureSuccessStatusCode();

        var resized = (await response.Content.ReadFromJsonAsync<BucketDetail>())!;
        Assert.Equal((100, 900, 400, 500), (resized.X, resized.Y, resized.Width, resized.Height));
        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        var note = snapshot!.Notes.Single(n => n.Id == inside.Id);
        Assert.Equal((240, 1040, frame.Id), (note.X, note.Y, note.BucketId));
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

        NoteDetail last = null!;
        for (var i = 1; i <= 5; i++) last = await CreateNote(board.Id, new { title = $"Task {i}" });

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal((60, 620, todo.Id), (last.X, last.Y, last.BucketId));
        Assert.Equal(720, snapshot!.Buckets[0].Height);
        Assert.Equal(5, snapshot.Notes.Count(n => n.BucketId == todo.Id));
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
    public async Task Search_finds_notes_by_title_or_content_case_insensitively()
    {
        var board = await CreateBoard("Search");
        await CreateNote(board.Id, new { title = "Design LOGO" });
        await CreateNote(board.Id, new { title = "Write copy", blocks = new[] { new { kind = "Text", text = "Mention the logo" } } });
        await CreateNote(board.Id, new { title = "Print", blocks = new[] { new { kind = "Checklist", items = new[] { new { text = "Logo on the cover" } } } } });
        await CreateNote(board.Id, new { title = "Brand", blocks = new[] { new { kind = "Links", links = new[] { new { title = "Logo files", url = "https://example.com" } } } } });
        await CreateNote(board.Id, new { title = "Book venue" });

        var hits = await _client.GetFromJsonAsync<List<NoteDetail>>($"/api/boards/{board.Id}/notes?search=logo");

        Assert.Equal(["Design LOGO", "Write copy", "Print", "Brand"], hits!.Select(n => n.Title));
    }

    [Fact]
    public async Task Notes_carry_tags_assignees_and_content_blocks()
    {
        var board = await CreateBoard("Details");
        var me = await _client.MeAsync();
        var created = await CreateNote(board.Id, new
        {
            title = "Retry policy for lidar tile ingest",
            tags = new[] { " Backend ", "ingest", "backend", "Tile Index" },
            assigneeIds = new[] { me.Id, me.Id },
            blocks = new object[]
            {
                new { kind = "Checklist", title = "Acceptance criteria", items = new object[] { new { text = "Backoff with jitter", done = true }, new { text = "Exhausted tiles go to the DLQ" } } },
                new { kind = "Links", links = new[] { new { title = "PR #412", url = "https://example.com/pulls/412" } } },
            },
        });

        Assert.Equal(["backend", "ingest", "tile-index"], created.Tags);
        Assert.Equal([new Assignee(me.Id, me.DisplayName, AssigneeKind.Person, null)], created.Assignees);
        var (checklist, links) = (created.Blocks[0], created.Blocks[1]);
        Assert.Equal((NoteBlockKind.Checklist, "Acceptance criteria", null), (checklist.Kind, checklist.Title, checklist.Text));
        Assert.Equal([new ChecklistItem("Backoff with jitter", true), new ChecklistItem("Exhausted tiles go to the DLQ")], checklist.Items!);
        Assert.Null(checklist.Links);
        Assert.Equal([new NoteLink("PR #412", "https://example.com/pulls/412")], links.Links!);
        Assert.False(created.AgentWorking);

        var response = await _client.PatchAsJsonAsync($"/api/notes/{created.Id}/blocks/{checklist.Id}", new
        {
            items = new object[] { new { text = "Backoff with jitter", done = true }, new { text = "Exhausted tiles go to the DLQ", done = true } },
        });
        response.EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/notes/{created.Id}", new { agentWorking = true })).EnsureSuccessStatusCode();
        var updated = (await _client.GetFromJsonAsync<NoteDetail>($"/api/notes/{created.Id}"))!;

        Assert.All(updated.Blocks[0].Items!, item => Assert.True(item.Done));
        Assert.True(updated.AgentWorking);
        Assert.Equal(created.Tags, updated.Tags);
        Assert.Equal(created.Assignees, updated.Assignees);
        Assert.Equal((links.Id, links.X, links.Y), (updated.Blocks[1].Id, updated.Blocks[1].X, updated.Blocks[1].Y));
        Assert.Equal(links.Links!, updated.Blocks[1].Links!);
    }

    [Fact]
    public async Task Notes_link_other_notes_by_their_path()
    {
        var board = await CreateBoard("Note links");
        var target = await CreateNote(board.Id, new { title = "Retry policy" });
        Assert.Equal($"/#/boards/{board.Id}/notes/{target.Id}", target.Path);

        var linking = await CreateNote(board.Id, new { title = "Follow-up", blocks = new[] { LinksBlock("Retry policy", target.Path) } });
        Assert.Equal([new NoteLink("Retry policy", target.Path)], linking.Blocks.Single().Links!);

        var notesUrl = $"/api/boards/{board.Id}/notes";
        var relative = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", blocks = new[] { LinksBlock("x", "/somewhere") } });
        var notAnId = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", blocks = new[] { LinksBlock("x", $"/#/boards/{board.Id}/notes/abc") } });
        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAnId.StatusCode);
    }

    [Fact]
    public async Task Note_details_are_validated()
    {
        var board = await CreateBoard("Detail validation");
        var notesUrl = $"/api/boards/{board.Id}/notes";

        var scriptLink = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", blocks = new[] { LinksBlock("x", "javascript:alert(1)") } });
        var longTag = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", tags = new[] { new string('t', 31) } });
        var stranger = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", assigneeIds = new[] { Guid.NewGuid() } });
        var tooManyPeople = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", assigneeIds = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()) });
        var tooManyTags = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", tags = Enumerable.Range(0, 11).Select(i => $"t{i}") });
        var misfit = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", blocks = new object[] { new { kind = "Text" }, new { kind = "Text", items = new[] { new { text = "x" } } } } });
        var longItem = await _client.PostAsJsonAsync(notesUrl, new { title = "Ok", blocks = new[] { new { kind = "Checklist", items = new[] { new { text = new string('x', 301) } } } } });

        Assert.Equal(HttpStatusCode.BadRequest, scriptLink.StatusCode);
        Assert.Contains("blocks[0].links", await scriptLink.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, longTag.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.Contains("assigneeIds", await stranger.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, tooManyPeople.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyTags.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, misfit.StatusCode);
        Assert.Contains("blocks[1].items", await misfit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, longItem.StatusCode);
    }

    [Fact]
    public async Task Notes_can_be_filtered_by_tags_and_assignee()
    {
        var board = await CreateBoard("Filters");
        var mila = await factory.CreateSignedInClientAsync("mila-filters@example.com");
        await mila.RenameAsync("Mila V.");
        await _client.ShareAsync(board.Id, mila, BoardRole.Editor);
        var milaId = (await mila.MeAsync()).Id;
        var meId = (await _client.MeAsync()).Id;
        await CreateNote(board.Id, new { title = "Ingest", tags = new[] { "backend" }, assigneeIds = new[] { meId } });
        await CreateNote(board.Id, new { title = "Dashboard", tags = new[] { "observability" }, assigneeIds = new[] { milaId } });
        await CreateNote(board.Id, new { title = "DLQ", tags = new[] { "backend" }, assigneeIds = new[] { milaId, meId } });
        await CreateNote(board.Id, new { title = "Docs", tags = new[] { "docs" } });

        var url = $"/api/boards/{board.Id}/notes";
        var backend = await _client.GetFromJsonAsync<List<NoteDetail>>($"{url}?tag=Backend");
        var either = await _client.GetFromJsonAsync<List<NoteDetail>>($"{url}?tag=backend&tag=observability");
        var byName = await _client.GetFromJsonAsync<List<NoteDetail>>($"{url}?assignee=mila%20v.");
        var byId = await _client.GetFromJsonAsync<List<NoteDetail>>($"{url}?assignee={milaId}");

        Assert.Equal(["Ingest", "DLQ"], backend!.Select(n => n.Title));
        Assert.Equal(["Ingest", "Dashboard", "DLQ"], either!.Select(n => n.Title));
        Assert.Equal(["Dashboard", "DLQ"], byName!.Select(n => n.Title));
        Assert.Equal(["Dashboard", "DLQ"], byId!.Select(n => n.Title));
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
                "createNote", "moveNote", "updateNote", "deleteNote", "restoreNote", "setNoteWorking", "restoreBucket",
                "addNoteBlock", "updateNoteBlock", "deleteNoteBlock",
                "listBoardTags", "createBoardTag", "updateBoardTag", "deleteBoardTag", "restoreBoardTag",
                "whoAmI", "listAuthProviders", "getVersion",
                "uploadImage", "getImageContent", "updateImage", "deleteImage", "restoreImage",
                "adminListBoards", "adminDeleteBoard", "adminListUsers", "adminSetBoardMember", "adminRemoveBoardMember",
                "adminUpdateUser", "adminDeleteUser",
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

    private static object LinksBlock(string title, string url) => new { kind = "Links", links = new[] { new { title, url } } };

    private async Task<NoteDetail> MoveNote(Guid noteId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/notes/{noteId}/move", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }
}
