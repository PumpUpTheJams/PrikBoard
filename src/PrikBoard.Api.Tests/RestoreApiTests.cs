using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Deleted notes, buckets, images and tags can be restored with the same id (what undo in the web app uses).</summary>
public class RestoreApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_deleted_note_is_hidden_everywhere_and_comes_back_whole_with_its_images()
    {
        var board = await _client.CreateBoardAsync("Restore a note");
        var created = await CreateNote(board.Id, new { title = "Ship the beta", tags = new[] { "release" }, blocks = new[] { new { kind = "Text", text = "Checklist for launch" } } });
        var note = await Note(created.Id); // as stored, with CreatedAt in microseconds
        var attached = await Upload(board.Id, $"?noteId={note.Id}");
        var removedEarlier = await Upload(board.Id, $"?noteId={note.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/images/{removedEarlier.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        var hidden = await Board(board.Id);
        Assert.Empty(hidden.Notes);
        Assert.Empty(hidden.Images);
        Assert.Equal(0, Assert.Single(hidden.Tags).NoteCount);
        Assert.Empty((await _client.GetFromJsonAsync<List<NoteDetail>>($"/api/boards/{board.Id}/notes?search=launch"))!);
        Assert.DoesNotContain("Ship the beta", await _client.GetStringAsync($"/api/boards/{board.Id}/export"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(attached.ContentUrl)).StatusCode);
        // Its images come back with the note, not on their own.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/images/{attached.Id}/restore", null)).StatusCode);

        var response = await _client.PostAsync($"/api/notes/{note.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var restored = (await response.Content.ReadFromJsonAsync<NoteDetail>())!;

        Assert.Equal((note.Id, note.CreatedById, note.CreatedAt, note.X, note.Y), (restored.Id, restored.CreatedById, restored.CreatedAt, restored.X, restored.Y));
        Assert.Equal(note.BucketId, restored.BucketId);
        Assert.Equal(["release"], restored.Tags);
        Assert.Equal(note.Blocks.Single().Id, Assert.Single(restored.Blocks).Id);
        var back = await Board(board.Id);
        Assert.Equal(note.Id, Assert.Single(back.Notes).Id);
        Assert.Equal(attached.Id, Assert.Single(back.Images).Id); // the image deleted on its own earlier stays deleted
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(attached.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/notes/{note.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_restored_note_joins_the_bucket_at_its_place_now()
    {
        var board = await _client.CreateBoardAsync("Restore into a bucket");
        var toDo = board.Buckets[0];
        var note = await _client.CreateNoteAsync(board.Id);
        Assert.Equal(toDo.Id, note.BucketId);

        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = 4000, y = 4000 })).EnsureSuccessStatusCode();
        var restored = await Restore<NoteDetail>($"/api/notes/{note.Id}/restore");

        Assert.Equal((note.X, note.Y), (restored.X, restored.Y)); // a deleted note is not carried along
        Assert.Null(restored.BucketId);
    }

    [Fact]
    public async Task A_restored_bucket_keeps_its_place_in_the_stack_and_takes_its_notes_back()
    {
        var board = await _client.CreateBoardAsync("Restore a bucket");
        var toDo = board.Buckets[0];
        var note = await _client.CreateNoteAsync(board.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/buckets/{toDo.Id}")).StatusCode);
        Assert.Null((await Board(board.Id)).Notes.Single().BucketId);
        var restored = await Restore<BucketDetail>($"/api/buckets/{toDo.Id}/restore");

        Assert.Equal((toDo.Name, toDo.X, toDo.Y, toDo.Width, toDo.Height), (restored.Name, restored.X, restored.Y, restored.Width, restored.Height));
        var back = await Board(board.Id);
        Assert.Equal(board.Buckets.Select(b => b.Id), back.Buckets.Select(b => b.Id));
        Assert.Equal(toDo.Id, back.Notes.Single(n => n.Id == note.Id).BucketId);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/buckets/{toDo.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_restored_tag_goes_back_on_the_notes_it_was_removed_from_even_deleted_ones()
    {
        var board = await _client.CreateBoardAsync("Restore a tag");
        var live = await CreateNote(board.Id, new { title = "Live", tags = new[] { "urgent", "api" } });
        var deleted = await CreateNote(board.Id, new { title = "Deleted", tags = new[] { "urgent" } });
        var untagged = await CreateNote(board.Id, new { title = "Untagged" });
        var tag = (await Board(board.Id)).Tags.Single(t => t.Name == "urgent");
        (await _client.DeleteAsync($"/api/notes/{deleted.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/boards/{board.Id}/tags/{tag.Id}")).StatusCode);
        Assert.Equal(["api"], (await Note(live.Id)).Tags);
        var restored = await Restore<BoardTagDetail>($"/api/boards/{board.Id}/tags/{tag.Id}/restore");

        Assert.Equal((tag.Id, "urgent", 1), (restored.Id, restored.Name, restored.NoteCount));
        Assert.Equal(["api", "urgent"], (await Note(live.Id)).Tags);
        Assert.Empty((await Note(untagged.Id)).Tags);
        Assert.Equal(["urgent"], (await Restore<NoteDetail>($"/api/notes/{deleted.Id}/restore")).Tags);
    }

    [Fact]
    public async Task A_deleted_tag_name_can_be_used_again_and_then_the_old_tag_cannot_come_back()
    {
        var board = await _client.CreateBoardAsync("Tag name reused");
        await CreateNote(board.Id, new { title = "Tagged", tags = new[] { "design" } });
        var old = (await Board(board.Id)).Tags.Single();
        (await _client.DeleteAsync($"/api/boards/{board.Id}/tags/{old.Id}")).EnsureSuccessStatusCode();

        var again = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "design" });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(old.Id, (await again.Content.ReadFromJsonAsync<BoardTagDetail>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/boards/{board.Id}/tags/{old.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Renaming_a_tag_reaches_deleted_notes_too()
    {
        var board = await _client.CreateBoardAsync("Rename while deleted");
        var note = await CreateNote(board.Id, new { title = "Old tag", tags = new[] { "old" } });
        var tag = (await Board(board.Id)).Tags.Single();
        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();

        (await _client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{tag.Id}", new { name = "new" })).EnsureSuccessStatusCode();

        Assert.Equal(["new"], (await Restore<NoteDetail>($"/api/notes/{note.Id}/restore")).Tags);
    }

    [Fact]
    public async Task Deleted_things_cannot_be_changed_and_unknown_ones_cannot_be_restored()
    {
        var board = await _client.CreateBoardAsync("Gone");
        var note = await _client.CreateNoteAsync(board.Id);
        var image = await Upload(board.Id);
        var bucket = board.Buckets[0];
        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/images/{image.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/buckets/{bucket.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/notes/{note.Id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/notes/{note.Id}/move", new { x = 0, y = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/notes/{note.Id}/blocks", new { kind = "Text" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { x = 0, y = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/buckets/{bucket.Id}", new { name = "x" })).StatusCode);

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/notes/{unknown}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/buckets/{unknown}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/images/{unknown}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/boards/{board.Id}/tags/{unknown}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/images/{image.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Restoring_needs_the_editor_role_and_hides_deleted_things_from_strangers()
    {
        var board = await _client.CreateBoardAsync("Restore roles");
        var viewer = await factory.CreateSignedInClientAsync("restore-viewer@example.com");
        var stranger = await factory.CreateSignedInClientAsync("restore-stranger@example.com");
        await _client.ShareAsync(board.Id, viewer, BoardRole.Viewer);
        var note = await _client.CreateNoteAsync(board.Id);
        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/notes/{note.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/notes/{note.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Things_deleted_longer_ago_than_the_retention_are_purged_for_good()
    {
        var board = await _client.CreateBoardAsync("Purge");
        var old = await _client.CreateNoteAsync(board.Id, "Old");
        var recent = await _client.CreateNoteAsync(board.Id, "Recent");
        var image = await Upload(board.Id, $"?noteId={old.Id}");
        (await _client.DeleteAsync($"/api/notes/{old.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/notes/{recent.Id}")).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var longAgo = DateTimeOffset.UtcNow - Tombstones.Retention - TimeSpan.FromDays(1);
            await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).Where(n => n.Id == old.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, longAgo));
            await db.Images.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).Where(i => i.Id == image.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, longAgo));

            var (file, path) = await factory.ImageFileAsync(image.Id);
            Assert.Equal(2, await Tombstones.PurgeAsync(db, DateTimeOffset.UtcNow - Tombstones.Retention, CancellationToken.None));

            // The purged image's file goes at the next sweep.
            Assert.True(File.Exists(path));
            Assert.True(await factory.SweepFilesAsync() >= 1);
            Assert.False(await db.StoredFiles.AnyAsync(f => f.Id == file.Id));
            Assert.False(File.Exists(path));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/notes/{old.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/notes/{recent.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_bucket_move_with_carry_note_ids_takes_exactly_those_notes_along()
    {
        var board = await _client.CreateBoardAsync("Carry");
        var other = await _client.CreateBoardAsync("Elsewhere");
        var toDo = board.Buckets[0];
        var carried = await _client.CreateNoteAsync(board.Id, "Carried");
        var stays = await _client.CreateNoteAsync(board.Id, "Stays");
        var foreign = await _client.CreateNoteAsync(other.Id, "Foreign");

        var moved = await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = toDo.X + 2000, carryNoteIds = new[] { carried.Id, foreign.Id } });
        var resize = await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = 0, width = 400, carryNoteIds = new[] { carried.Id } });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal((carried.X + 2000, toDo.Id), ((await Note(carried.Id)).X, (await Note(carried.Id)).BucketId));
        Assert.Equal((stays.X, (Guid?)null), ((await Note(stays.Id)).X, (await Note(stays.Id)).BucketId));
        Assert.Equal(foreign.X, (await Note(foreign.Id)).X);
        Assert.Equal(HttpStatusCode.BadRequest, resize.StatusCode);
    }

    [Fact]
    public async Task A_removed_block_can_be_put_back_with_its_id()
    {
        var board = await _client.CreateBoardAsync("Block back");
        var note = await CreateNote(board.Id, new { title = "Blocks", blocks = new[] { new { kind = "Text", text = "Keep me" } } });
        var block = note.Blocks.Single();
        (await _client.DeleteAsync($"/api/notes/{note.Id}/blocks/{block.Id}")).EnsureSuccessStatusCode();

        var back = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/blocks",
            new { id = block.Id, kind = "Text", text = block.Text, x = block.X, y = block.Y, width = block.Width, height = block.Height });
        var twice = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/blocks", new { id = block.Id, kind = "Text" });

        Assert.Equal(HttpStatusCode.Created, back.StatusCode);
        var restored = (await back.Content.ReadFromJsonAsync<NoteBlockDetail>())!;
        Assert.Equal((block.Id, block.Text, block.X, block.Y, block.Width, block.Height),
            (restored.Id, restored.Text, restored.X, restored.Y, restored.Width, restored.Height));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    private async Task<NoteDetail> CreateNote(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/notes", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }

    private async Task<BoardDetail> Board(Guid boardId) => (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{boardId}"))!;

    private async Task<NoteDetail> Note(Guid noteId) => (await _client.GetFromJsonAsync<NoteDetail>($"/api/notes/{noteId}"))!;

    private async Task<T> Restore<T>(string url)
    {
        var response = await _client.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
