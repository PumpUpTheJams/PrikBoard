using System.Net;
using System.Net.Http.Json;
using System.Text;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Short board ids, board keys and card numbers: the parts of a card's path and readable id (MPCT-415).</summary>
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
        var card = await owner.CreateCardAsync(board.Id);
        Assert.Equal("MMST-1", card.ReadableId);

        var renamed = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { name = "PrikBoard", key = "mpct" });
        renamed.EnsureSuccessStatusCode();
        var summary = (await renamed.Content.ReadFromJsonAsync<BoardSummary>())!;
        Assert.Equal((board.ShortId, "MPCT", "PrikBoard"), (summary.ShortId, summary.Key, summary.Name));
        var after = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!;
        Assert.Equal(("MPCT-1", card.Path), (after.ReadableId, after.Path));

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
    public async Task Cards_are_numbered_per_board_and_a_number_is_never_used_twice()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-numbers@example.com");
        var board = await owner.CreateBoardAsync("Numbers");
        var other = await owner.CreateBoardAsync("Other numbers");

        var cards = new List<CardDetail>();
        for (var i = 0; i < 3; i++) cards.Add(await owner.CreateCardAsync(board.Id));
        Assert.Equal([1, 2, 3], cards.Select(n => n.Number));
        Assert.Equal(1, (await owner.CreateCardAsync(other.Id)).Number);

        (await owner.DeleteAsync($"/api/cards/{cards[2].Id}")).EnsureSuccessStatusCode();
        Assert.Equal(4, (await owner.CreateCardAsync(board.Id)).Number);
        var restored = await owner.PostAsJsonAsync($"/api/cards/{cards[2].Id}/restore", new { });
        restored.EnsureSuccessStatusCode();
        Assert.Equal(3, (await restored.Content.ReadFromJsonAsync<CardDetail>())!.Number);

        var together = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => owner.CreateCardAsync(board.Id)));
        Assert.Equal(Enumerable.Range(5, 10), together.Select(n => n.Number).Order());
    }

    [Fact]
    public async Task A_card_is_found_by_the_short_id_and_number_in_its_path()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-lookup@example.com");
        var board = await owner.CreateBoardAsync("Lookup");
        var first = await owner.CreateCardAsync(board.Id, "First");
        var second = await owner.CreateCardAsync(board.Id, "Second");

        var found = (await owner.GetFromJsonAsync<CardDetail>($"/api/boards/{board.ShortId}/cards/2"))!;
        Assert.Equal((second.Id, $"/#/boards/{board.ShortId}/cards/2"), (found.Id, found.Path));

        var stranger = await factory.CreateSignedInClientAsync("ids-stranger@example.com");
        (await owner.DeleteAsync($"/api/cards/{first.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.ShortId}/cards/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.ShortId}/cards/9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/boards/zzzzzz/cards/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/boards/{board.ShortId}/cards/2")).StatusCode);
    }

    [Fact]
    public async Task Links_in_the_older_form_to_cards_on_the_board_are_stored_as_paths()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-links@example.com");
        var board = await owner.CreateBoardAsync("Links");
        var elsewhere = await owner.CreateBoardAsync("Elsewhere");
        var target = await owner.CreateCardAsync(board.Id, "Target");
        var away = await owner.CreateCardAsync(elsewhere.Id, "Away");
        var old = $"/#/boards/{board.Id}/cards/{target.Id}";
        var oldAway = $"/#/boards/{elsewhere.Id}/cards/{away.Id}";

        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new
        {
            title = "Linking",
            components = new[] { new { kind = "Links", links = new[] { new { title = "Target", url = old }, new { title = "Away", url = oldAway } } } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var linking = (await response.Content.ReadFromJsonAsync<CardDetail>())!;
        // A link to another board stays as it was, so it reveals nothing new about that board (such as its short id).
        Assert.Equal([target.Path, oldAway], linking.Components.Single().Links!.Select(l => l.Url));

        var added = await owner.PostAsJsonAsync($"/api/cards/{linking.Id}/components", new { kind = "Links", links = new[] { new { title = "Again", url = old } } });
        Assert.Equal(target.Path, (await added.Content.ReadFromJsonAsync<CardComponentDetail>())!.Links!.Single().Url);
        var component = linking.Components.Single();
        var updated = await owner.PatchAsJsonAsync($"/api/cards/{linking.Id}/components/{component.Id}", new { links = new[] { new { title = "Target", url = $"  {old}  " } } });
        Assert.Equal(target.Path, (await updated.Content.ReadFromJsonAsync<CardComponentDetail>())!.Links!.Single().Url);
    }

    [Fact]
    public async Task An_imported_board_keeps_the_key_and_the_card_numbers()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-export@example.com");
        var board = await owner.CreateBoardAsync("Exported");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key = "EXP" })).EnsureSuccessStatusCode();
        var first = await owner.CreateCardAsync(board.Id, "First");
        var gone = await owner.CreateCardAsync(board.Id, "Gone");
        (await owner.DeleteAsync($"/api/cards/{gone.Id}")).EnsureSuccessStatusCode();
        var linking = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new
        {
            title = "Third",
            components = new[] { new { kind = "Links", links = new[] { new { title = "First", url = first.Path } } } },
        });
        linking.EnsureSuccessStatusCode();

        var file = await owner.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await owner.PostAsync("/api/boards/import", new StringContent(file, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal("EXP", copy.Key);
        Assert.NotEqual(board.ShortId, copy.ShortId);
        Assert.Equal([("First", 1), ("Third", 3)], copy.Cards.Select(n => (n.Title, n.Number)).Order());
        var copiedFirst = copy.Cards.Single(n => n.Title == "First");
        Assert.Equal($"/#/boards/{copy.ShortId}/cards/1", copiedFirst.Path);
        Assert.Equal(copiedFirst.Path, copy.Cards.Single(n => n.Title == "Third").Components.Single().Links!.Single().Url);
        Assert.Equal(4, (await owner.CreateCardAsync(copy.Id)).Number);
    }
}
