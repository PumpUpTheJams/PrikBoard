using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Board tags: the list notes pick their tags from, renamed and deleted for every note at once.</summary>
public class TagApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task Tags_used_on_notes_join_the_board_list()
    {
        var client = await factory.CreateSignedInClientAsync("tags-join@example.com");
        var board = await client.CreateBoardAsync("Tags join");

        await CreateNote(client, board.Id, "Ingest", "Back end", "ui");
        await CreateNote(client, board.Id, "Dashboard", "UI");

        var tags = await TagsAsync(client, board.Id);
        Assert.Equal([("back-end", 1), ("ui", 2)], tags.Select(t => (t.Name, t.NoteCount)));
        var snapshot = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(tags, snapshot.Tags);
    }

    [Fact]
    public async Task Board_tags_exist_without_notes()
    {
        var client = await factory.CreateSignedInClientAsync("tags-alone@example.com");
        var board = await client.CreateBoardAsync("Tags alone");

        var created = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = " Some Day " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tag = (await created.Content.ReadFromJsonAsync<BoardTagDetail>())!;
        Assert.Equal(("some-day", 0), (tag.Name, tag.NoteCount));

        var again = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "some day" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var blank = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        // Taking a tag off a note keeps it on the board.
        var note = await CreateNote(client, board.Id, "Later", "some-day");
        (await client.PatchAsJsonAsync($"/api/notes/{note.Id}", new { tags = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Assert.Equal([("some-day", 0)], (await TagsAsync(client, board.Id)).Select(x => (x.Name, x.NoteCount)));
    }

    [Fact]
    public async Task Renaming_a_tag_renames_it_on_every_note()
    {
        var client = await factory.CreateSignedInClientAsync("tags-rename@example.com");
        var board = await client.CreateBoardAsync("Tags rename");
        var first = await CreateNote(client, board.Id, "First", "ui", "backend");
        var second = await CreateNote(client, board.Id, "Second", "ui");
        var ui = (await TagsAsync(client, board.Id)).Single(t => t.Name == "ui");

        var renamed = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = "Front End" });
        renamed.EnsureSuccessStatusCode();

        Assert.Equal(new BoardTagDetail(ui.Id, "front-end", 2), await renamed.Content.ReadFromJsonAsync<BoardTagDetail>());
        Assert.Equal(["front-end", "backend"], (await NoteAsync(client, first.Id)).Tags);
        Assert.Equal(["front-end"], (await NoteAsync(client, second.Id)).Tags);
        Assert.Equal(["backend", "front-end"], (await TagsAsync(client, board.Id)).Select(t => t.Name));

        var clash = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = "backend" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var tooLong = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = new string('x', 31) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_from_every_note()
    {
        var client = await factory.CreateSignedInClientAsync("tags-delete@example.com");
        var board = await client.CreateBoardAsync("Tags delete");
        var first = await CreateNote(client, board.Id, "First", "ui", "backend");
        var second = await CreateNote(client, board.Id, "Second", "ui");
        var ui = (await TagsAsync(client, board.Id)).Single(t => t.Name == "ui");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/boards/{board.Id}/tags/{ui.Id}")).StatusCode);

        Assert.Equal(["backend"], (await NoteAsync(client, first.Id)).Tags);
        Assert.Empty((await NoteAsync(client, second.Id)).Tags);
        Assert.Equal(["backend"], (await TagsAsync(client, board.Id)).Select(t => t.Name));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/boards/{board.Id}/tags/{ui.Id}")).StatusCode);
    }

    [Fact]
    public async Task Tags_from_another_board_are_not_found()
    {
        var client = await factory.CreateSignedInClientAsync("tags-other@example.com");
        var mine = await client.CreateBoardAsync("Tags A");
        var other = await client.CreateBoardAsync("Tags B");
        await CreateNote(client, mine.Id, "Tagged", "ui");
        var ui = Assert.Single(await TagsAsync(client, mine.Id));

        var rename = await client.PatchAsJsonAsync($"/api/boards/{other.Id}/tags/{ui.Id}", new { name = "x" });
        var delete = await client.DeleteAsync($"/api/boards/{other.Id}/tags/{ui.Id}");

        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("ui", Assert.Single(await TagsAsync(client, mine.Id)).Name);
    }

    [Fact]
    public async Task A_board_holds_at_most_100_tags()
    {
        var client = await factory.CreateSignedInClientAsync("tags-limit@example.com");
        var board = await client.CreateBoardAsync("Tags limit");
        for (var i = 0; i < BoardTag.MaxPerBoard; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = $"t{i}" })).StatusCode);
        }

        var oneMore = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "extra" });
        var newOnNote = await client.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = "New", tags = new[] { "extra" } });
        var existingOnNote = await client.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = "Old", tags = new[] { "t1" } });

        Assert.Equal(HttpStatusCode.Conflict, oneMore.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, newOnNote.StatusCode);
        Assert.Contains("\"tags\"", await newOnNote.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, existingOnNote.StatusCode);
    }

    private static async Task<NoteDetail> CreateNote(HttpClient client, Guid boardId, string title, params string[] tags)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/notes", new { title, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }

    private static async Task<NoteDetail> NoteAsync(HttpClient client, Guid noteId) =>
        (await client.GetFromJsonAsync<NoteDetail>($"/api/notes/{noteId}"))!;

    private static async Task<List<BoardTagDetail>> TagsAsync(HttpClient client, Guid boardId) =>
        (await client.GetFromJsonAsync<List<BoardTagDetail>>($"/api/boards/{boardId}/tags"))!;
}
