using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The trash (notes deleted in the last days, put back from there) and buckets that fold up into a square.</summary>
public class TrashAndCollapseApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_trash_lists_deleted_notes_newest_first_with_their_images_and_searches_them()
    {
        var board = await _client.CreateBoardAsync("Trash");
        var first = await _client.CreateNoteAsync(board.Id, "Plan the launch party");
        var second = await _client.CreateNoteAsync(board.Id, "Book the venue");
        var kept = await _client.CreateNoteAsync(board.Id, "Stays on the board");
        var image = await Upload(board.Id, $"?noteId={second.Id}");
        var deletedEarlier = await Upload(board.Id, $"?noteId={second.Id}");
        (await _client.DeleteAsync($"/api/images/{deletedEarlier.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/notes/{first.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/notes/{second.Id}")).EnsureSuccessStatusCode();

        var trash = await Trash(board.Id);
        var found = await Trash(board.Id, "VENUE");

        Assert.Equal([second.Id, first.Id], trash.Notes.Select(n => n.Id));
        Assert.All(trash.Notes, n => Assert.NotNull(n.DeletedAt));
        Assert.Equal(image.Id, Assert.Single(trash.Images).Id); // the one deleted on its own earlier doesn't come back with it
        Assert.Equal(second.Id, Assert.Single(found.Notes).Id);
        Assert.DoesNotContain(trash.Notes, n => n.Id == kept.Id);
        Assert.Null((await _client.GetFromJsonAsync<NoteDetail>($"/api/notes/{kept.Id}"))!.DeletedAt);
    }

    [Fact]
    public async Task A_deleted_note_goes_back_where_it_was_or_where_it_is_dropped()
    {
        var board = await _client.CreateBoardAsync("Put back");
        var note = await _client.CreateNoteAsync(board.Id, "Back home");
        var dropped = await _client.CreateNoteAsync(board.Id, "Somewhere else");
        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/notes/{dropped.Id}")).EnsureSuccessStatusCode();

        var half = await _client.PostAsJsonAsync($"/api/notes/{dropped.Id}/restore", new { x = 10 });
        var back = await Ok<NoteDetail>(await _client.PostAsync($"/api/notes/{note.Id}/restore", null));
        var elsewhere = await Ok<NoteDetail>(await _client.PostAsJsonAsync($"/api/notes/{dropped.Id}/restore", new { x = 3011, y = 409 }));

        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
        Assert.Equal((note.X, note.Y, note.BucketId), (back.X, back.Y, back.BucketId));
        Assert.Equal((3020, 400, (Guid?)null), (elsewhere.X, elsewhere.Y, elsewhere.BucketId));
        Assert.True(elsewhere.UpdatedAt > dropped.UpdatedAt);
        Assert.Empty((await Trash(board.Id)).Notes);
    }

    [Fact]
    public async Task Viewers_read_the_trash_and_strangers_see_nothing()
    {
        var board = await _client.CreateBoardAsync("Trash roles");
        var viewer = await factory.CreateSignedInClientAsync("trash-viewer@example.com");
        var stranger = await factory.CreateSignedInClientAsync("trash-stranger@example.com");
        await _client.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/boards/{board.Id}/trash")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/boards/{board.Id}/trash")).StatusCode);
    }

    [Fact]
    public async Task A_collapsed_bucket_keeps_its_notes_in_place_and_takes_no_others()
    {
        var board = await _client.CreateBoardAsync("Collapse");
        var toDo = board.Buckets[0];
        var inside = await _client.CreateNoteAsync(board.Id, "Inside");

        var collapsed = await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { collapsed = true }));
        // A note put down where the collapsed bucket is stays loose (it would vanish otherwise)...
        var loose = await CreateNote(board.Id, new { title = "Loose", x = toDo.X + 20, y = toDo.Y + 300 });
        // ...but one sent into it by name goes in.
        var sent = await CreateNote(board.Id, new { title = "Sent", bucketId = toDo.Id });
        var resize = await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { width = 600 });
        var moved = await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = toDo.X + 1000 }));
        var opened = await Ok<BucketDetail>(await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { collapsed = false }));

        Assert.True(collapsed.Collapsed);
        Assert.Null(loose.BucketId);
        Assert.Equal(toDo.Id, sent.BucketId);
        Assert.Equal(HttpStatusCode.BadRequest, resize.StatusCode);
        Assert.False(opened.Collapsed);
        var after = (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        // Its own notes moved along with it while it was collapsed, the loose one didn't.
        Assert.Equal((inside.X + 1000, toDo.Id), after.Notes.Where(n => n.Id == inside.Id).Select(n => (n.X, n.BucketId)).Single());
        Assert.Equal((loose.X, (Guid?)null), after.Notes.Where(n => n.Id == loose.Id).Select(n => (n.X, n.BucketId)).Single());
        Assert.Equal(toDo.X + 1000, moved.X);
    }

    [Fact]
    public async Task A_collapsed_bucket_survives_export_and_import_with_its_notes()
    {
        var board = await _client.CreateBoardAsync("Collapse export");
        var toDo = board.Buckets[0];
        await _client.CreateNoteAsync(board.Id, "Inside");
        (await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { collapsed = true })).EnsureSuccessStatusCode();
        var file = await (await _client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();

        var response = await _client.PostAsync("/api/boards/import", new StringContent(file, System.Text.Encoding.UTF8, "application/json"));
        var imported = (await response.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal([true, false, false], imported.Buckets.Select(b => b.Collapsed));
        Assert.Equal(imported.Buckets[0].Id, Assert.Single(imported.Notes).BucketId);
    }

    private async Task<TrashContents> Trash(Guid boardId, string? search = null) =>
        (await _client.GetFromJsonAsync<TrashContents>($"/api/boards/{boardId}/trash{(search is null ? "" : $"?search={search}")}"))!;

    private async Task<NoteDetail> CreateNote(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/notes", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
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
