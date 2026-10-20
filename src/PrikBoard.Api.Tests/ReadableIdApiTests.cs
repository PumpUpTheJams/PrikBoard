using System.Net;
using System.Net.Http.Json;
using System.Text;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Short board ids, board keys and note numbers: the parts of a note's path and readable id (MPCT-415).</summary>
public class ReadableIdApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Theory]
    [InlineData("MCP me some tickets", "MMST")]
    [InlineData("PrikBoard", "PRIK")]
    [InlineData("Website redesign", "WR")]
    [InlineData("2026 plans", "PLAN")]
    [InlineData("Café & co", "CC")]
    [InlineData("A", "PB")]
    [InlineData("!!", "PB")]
    public void A_key_comes_from_the_board_name(string name, string key) => Assert.Equal(key, Board.KeyFrom(name));

    [Fact]
    public async Task Boards_get_a_short_id_that_never_changes_and_a_key_owners_can_change()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-owner@example.com");
        var board = await owner.CreateBoardAsync("MCP me some tickets");
        var other = await owner.CreateBoardAsync("MCP me some tickets");
        Assert.Equal("MMST", board.Key);
        Assert.Matches("^[0-9a-hjkmnp-tv-z]{6}$", board.ShortId);
        Assert.NotEqual(board.ShortId, other.ShortId);
        var note = await owner.CreateNoteAsync(board.Id);
        Assert.Equal("MMST-1", note.ReadableId);

        var renamed = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { name = "PrikBoard", key = "mpct" });
        renamed.EnsureSuccessStatusCode();
        var summary = (await renamed.Content.ReadFromJsonAsync<BoardSummary>())!;
        Assert.Equal((board.ShortId, "MPCT", "PrikBoard"), (summary.ShortId, summary.Key, summary.Name));
        var after = (await owner.GetFromJsonAsync<NoteDetail>($"/api/notes/{note.Id}"))!;
        Assert.Equal(("MPCT-1", note.Path), (after.ReadableId, after.Path));

        foreach (var key in new[] { "1AB", "A", "TOOLONGAKEY", "MP-CT", "" })
        {
            var response = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, key);
        }
        var editor = await factory.CreateSignedInClientAsync("ids-editor@example.com");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key = "EDIT" })).StatusCode);
    }

    [Fact]
    public async Task Notes_are_numbered_per_board_and_a_number_is_never_used_twice()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-numbers@example.com");
        var board = await owner.CreateBoardAsync("Numbers");
        var other = await owner.CreateBoardAsync("Other numbers");

        var notes = new List<NoteDetail>();
        for (var i = 0; i < 3; i++) notes.Add(await owner.CreateNoteAsync(board.Id));
        Assert.Equal([1, 2, 3], notes.Select(n => n.Number));
        Assert.Equal(1, (await owner.CreateNoteAsync(other.Id)).Number);

        (await owner.DeleteAsync($"/api/notes/{notes[2].Id}")).EnsureSuccessStatusCode();
        Assert.Equal(4, (await owner.CreateNoteAsync(board.Id)).Number);
        var restored = await owner.PostAsJsonAsync($"/api/notes/{notes[2].Id}/restore", new { });
        restored.EnsureSuccessStatusCode();
        Assert.Equal(3, (await restored.Content.ReadFromJsonAsync<NoteDetail>())!.Number);

        var together = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => owner.CreateNoteAsync(board.Id)));
        Assert.Equal(Enumerable.Range(5, 10), together.Select(n => n.Number).Order());
    }

    [Fact]
    public async Task A_note_is_found_by_the_short_id_and_number_in_its_path()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-lookup@example.com");
        var board = await owner.CreateBoardAsync("Lookup");
        var first = await owner.CreateNoteAsync(board.Id, "First");
        var second = await owner.CreateNoteAsync(board.Id, "Second");

        var found = (await owner.GetFromJsonAsync<NoteDetail>($"/api/boards/{board.ShortId}/notes/2"))!;
        Assert.Equal((second.Id, $"/#/boards/{board.ShortId}/notes/2"), (found.Id, found.Path));

        var stranger = await factory.CreateSignedInClientAsync("ids-stranger@example.com");
        (await owner.DeleteAsync($"/api/notes/{first.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.ShortId}/notes/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.ShortId}/notes/9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/boards/zzzzzz/notes/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/boards/{board.ShortId}/notes/2")).StatusCode);
    }

    [Fact]
    public async Task Links_in_the_older_form_to_notes_on_the_board_are_stored_as_paths()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-links@example.com");
        var board = await owner.CreateBoardAsync("Links");
        var elsewhere = await owner.CreateBoardAsync("Elsewhere");
        var target = await owner.CreateNoteAsync(board.Id, "Target");
        var away = await owner.CreateNoteAsync(elsewhere.Id, "Away");
        var old = $"/#/boards/{board.Id}/notes/{target.Id}";
        var oldAway = $"/#/boards/{elsewhere.Id}/notes/{away.Id}";

        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new
        {
            title = "Linking",
            blocks = new[] { new { kind = "Links", links = new[] { new { title = "Target", url = old }, new { title = "Away", url = oldAway } } } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var linking = (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
        // A link to another board stays as it was, so it says nothing new about that board.
        Assert.Equal([target.Path, oldAway], linking.Blocks.Single().Links!.Select(l => l.Url));

        var added = await owner.PostAsJsonAsync($"/api/notes/{linking.Id}/blocks", new { kind = "Links", links = new[] { new { title = "Again", url = old } } });
        Assert.Equal(target.Path, (await added.Content.ReadFromJsonAsync<NoteBlockDetail>())!.Links!.Single().Url);
        var block = linking.Blocks.Single();
        var updated = await owner.PatchAsJsonAsync($"/api/notes/{linking.Id}/blocks/{block.Id}", new { links = new[] { new { title = "Target", url = $"  {old}  " } } });
        Assert.Equal(target.Path, (await updated.Content.ReadFromJsonAsync<NoteBlockDetail>())!.Links!.Single().Url);
    }

    [Fact]
    public async Task An_imported_board_keeps_the_key_and_the_note_numbers()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-export@example.com");
        var board = await owner.CreateBoardAsync("Exported");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key = "EXP" })).EnsureSuccessStatusCode();
        var first = await owner.CreateNoteAsync(board.Id, "First");
        var gone = await owner.CreateNoteAsync(board.Id, "Gone");
        (await owner.DeleteAsync($"/api/notes/{gone.Id}")).EnsureSuccessStatusCode();
        var linking = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new
        {
            title = "Third",
            blocks = new[] { new { kind = "Links", links = new[] { new { title = "First", url = first.Path } } } },
        });
        linking.EnsureSuccessStatusCode();

        var file = await owner.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await owner.PostAsync("/api/boards/import", new StringContent(file, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal("EXP", copy.Key);
        Assert.NotEqual(board.ShortId, copy.ShortId);
        Assert.Equal([("First", 1), ("Third", 3)], copy.Notes.Select(n => (n.Title, n.Number)).Order());
        var copiedFirst = copy.Notes.Single(n => n.Title == "First");
        Assert.Equal($"/#/boards/{copy.ShortId}/notes/1", copiedFirst.Path);
        Assert.Equal(copiedFirst.Path, copy.Notes.Single(n => n.Title == "Third").Blocks.Single().Links!.Single().Url);
        Assert.Equal(4, (await owner.CreateNoteAsync(copy.Id)).Number);
    }
}
