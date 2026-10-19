using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class NoteBlockApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task New_blocks_get_their_default_size_and_a_free_spot_next_to_each_other()
    {
        var note = await NewNote();

        var text = await Add(note.Id, new { kind = "Text", text = "Retry tiles", title = "  Context " });
        var checklist = await Add(note.Id, new { kind = "Checklist" });
        var links = await Add(note.Id, new { kind = "Links" });

        Assert.Equal((20, 20, 340, 220, "Context", "Retry tiles"), (text.X, text.Y, text.Width, text.Height, text.Title, text.Text));
        Assert.Equal((380, 20, 340, 260), (checklist.X, checklist.Y, checklist.Width, checklist.Height));
        // 740 + 360 still fits the 1100 px row.
        Assert.Equal((740, 20, 360, 220), (links.X, links.Y, links.Width, links.Height));
        Assert.Null(checklist.Text);
        Assert.Empty(checklist.Items!);
        Assert.Null(checklist.Links);

        var fourth = await Add(note.Id, new { kind = "Text" });
        Assert.Equal((20, 260), (fourth.X, fourth.Y));
    }

    [Fact]
    public async Task Blocks_move_and_resize_on_the_grid_within_limits()
    {
        var note = await NewNote();
        var block = await Add(note.Id, new { kind = "Text", x = 131, y = 289, width = 401, height = 50 });
        Assert.Equal((140, 280, 400, 80), (block.X, block.Y, block.Width, block.Height));

        var moved = await Update(note.Id, block.Id, new { x = 10_000, y = 9, width = 150 });

        Assert.Equal((10_000 - 160, 0, 160, 80), (moved.X, moved.Y, moved.Width, moved.Height));
    }

    [Fact]
    public async Task Content_must_fit_the_kind_and_can_be_cleared()
    {
        var note = await NewNote();
        var text = await Add(note.Id, new { kind = "Text", text = "Draft", title = "Notes" });
        var url = $"/api/notes/{note.Id}/blocks/{text.Id}";

        var items = await _client.PatchAsJsonAsync(url, new { items = new[] { new { text = "x" } } });
        var noKind = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/blocks", new { text = "x" });
        var xOnly = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/blocks", new { kind = "Text", x = 40 });
        var cleared = await Update(note.Id, text.Id, new { text = "", title = " " });

        Assert.Equal(HttpStatusCode.BadRequest, items.StatusCode);
        Assert.Contains("items", await items.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, xOnly.StatusCode);
        Assert.Equal(("", null), (cleared.Text, cleared.Title));
    }

    [Fact]
    public async Task Blocks_are_deleted_and_unknown_ones_are_not_found()
    {
        var note = await NewNote();
        var other = await NewNote();
        var block = await Add(note.Id, new { kind = "Links" });

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/notes/{other.Id}/blocks/{block.Id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/notes/{note.Id}/blocks/{block.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/notes/{note.Id}/blocks/{block.Id}")).StatusCode);
        Assert.Empty((await Get(note.Id)).Blocks);
    }

    [Fact]
    public async Task A_note_holds_at_most_thirty_blocks()
    {
        var note = await NewNote();
        for (var i = 0; i < NoteBlock.MaxBlocks; i++) await Add(note.Id, new { kind = "Text" });

        var response = await _client.PostAsJsonAsync($"/api/notes/{note.Id}/blocks", new { kind = "Text" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Edits_to_different_blocks_at_the_same_time_both_survive()
    {
        var note = await NewNote();
        var blocks = new List<NoteBlockDetail>();
        for (var i = 0; i < 6; i++) blocks.Add(await Add(note.Id, new { kind = "Text" }));

        await Task.WhenAll(blocks.Select((b, i) => _client.PatchAsJsonAsync($"/api/notes/{note.Id}/blocks/{b.Id}", new { text = $"Edit {i}" })));

        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"Edit {i}"), (await Get(note.Id)).Blocks.Select(b => b.Text));
    }

    private async Task<NoteDetail> NewNote()
    {
        var board = await _client.CreateBoardAsync("Blocks");
        return await _client.CreateNoteAsync(board.Id);
    }

    private async Task<NoteDetail> Get(Guid noteId) => (await _client.GetFromJsonAsync<NoteDetail>($"/api/notes/{noteId}"))!;

    private async Task<NoteBlockDetail> Add(Guid noteId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/notes/{noteId}/blocks", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteBlockDetail>())!;
    }

    private async Task<NoteBlockDetail> Update(Guid noteId, Guid blockId, object request)
    {
        var response = await _client.PatchAsJsonAsync($"/api/notes/{noteId}/blocks/{blockId}", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteBlockDetail>())!;
    }
}
