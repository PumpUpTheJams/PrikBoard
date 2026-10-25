using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Board tags: the list cards pick their tags from, renamed and deleted for every card at once.</summary>
public class TagApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task Tags_used_on_cards_join_the_board_list()
    {
        var client = await factory.CreateSignedInClientAsync("tags-join@example.com");
        var board = await client.CreateBoardAsync("Tags join");

        await CreateCard(client, board.Id, "Ingest", "Back end", "ui");
        await CreateCard(client, board.Id, "Dashboard", "UI");

        var tags = await TagsAsync(client, board.Id);
        Assert.Equal([("back-end", 1), ("ui", 2)], tags.Select(t => (t.Name, t.CardCount)));
        var snapshot = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(tags, snapshot.Tags);
    }

    [Fact]
    public async Task Board_tags_exist_without_cards()
    {
        var client = await factory.CreateSignedInClientAsync("tags-alone@example.com");
        var board = await client.CreateBoardAsync("Tags alone");

        var created = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = " Some Day " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tag = (await created.Content.ReadFromJsonAsync<BoardTagDetail>())!;
        Assert.Equal(("some-day", 0), (tag.Name, tag.CardCount));

        var again = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "some day" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var blank = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        // Taking a tag off a card keeps it on the board.
        var card = await CreateCard(client, board.Id, "Later", "some-day");
        (await client.PatchAsJsonAsync($"/api/cards/{card.Id}", new { tags = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Assert.Equal([("some-day", 0)], (await TagsAsync(client, board.Id)).Select(x => (x.Name, x.CardCount)));
    }

    [Fact]
    public async Task Renaming_a_tag_renames_it_on_every_card_and_deleting_it_removes_it_from_every_card()
    {
        var client = await factory.CreateSignedInClientAsync("tags-rename@example.com");
        var board = await client.CreateBoardAsync("Tags rename");
        var first = await CreateCard(client, board.Id, "First", "ui", "backend");
        var second = await CreateCard(client, board.Id, "Second", "ui");
        var ui = (await TagsAsync(client, board.Id)).Single(t => t.Name == "ui");

        var renamed = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = "Front End" });
        renamed.EnsureSuccessStatusCode();

        Assert.Equal(new BoardTagDetail(ui.Id, "front-end", TagColor.Default, 2), await renamed.Content.ReadFromJsonAsync<BoardTagDetail>());
        Assert.Equal(["front-end", "backend"], (await CardAsync(client, first.Id)).Tags);
        Assert.Equal(["front-end"], (await CardAsync(client, second.Id)).Tags);
        Assert.Equal(["backend", "front-end"], (await TagsAsync(client, board.Id)).Select(t => t.Name));

        var clash = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = "backend" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var tooLong = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = new string('x', 31) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/boards/{board.Id}/tags/{ui.Id}")).StatusCode);

        Assert.Equal(["backend"], (await CardAsync(client, first.Id)).Tags);
        Assert.Empty((await CardAsync(client, second.Id)).Tags);
        Assert.Equal(["backend"], (await TagsAsync(client, board.Id)).Select(t => t.Name));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/boards/{board.Id}/tags/{ui.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_tag_gets_a_color_of_its_own_and_back_to_default()
    {
        var client = await factory.CreateSignedInClientAsync("tags-color@example.com");
        var board = await client.CreateBoardAsync("Tags color");
        var card = await CreateCard(client, board.Id, "Urgent", "ui");
        var ui = (await TagsAsync(client, board.Id)).Single();
        Assert.Equal(TagColor.Default, ui.Color);

        var created = await client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "urgent", color = "Brick" });
        Assert.Equal(TagColor.Brick, (await created.Content.ReadFromJsonAsync<BoardTagDetail>())!.Color);

        // Only the color: the name and the cards stay as they are.
        var recolored = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { color = "Ocean" });
        recolored.EnsureSuccessStatusCode();
        Assert.Equal(new BoardTagDetail(ui.Id, "ui", TagColor.Ocean, 1), await recolored.Content.ReadFromJsonAsync<BoardTagDetail>());
        Assert.Equal(["ui"], (await CardAsync(client, card.Id)).Tags);

        // A new name keeps the color; both at once change both.
        var renamed = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = "front-end" });
        Assert.Equal(TagColor.Ocean, (await renamed.Content.ReadFromJsonAsync<BoardTagDetail>())!.Color);
        var both = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = "ui", color = "Default" });
        var reset = (await both.Content.ReadFromJsonAsync<BoardTagDetail>())!;
        Assert.Equal(("ui", TagColor.Default), (reset.Name, reset.Color));

        var unknown = await client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { color = "Neon" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var snapshot = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal([("ui", TagColor.Default), ("urgent", TagColor.Brick)], snapshot.Tags.Select(x => (x.Name, x.Color)));
    }

    [Fact]
    public async Task Tags_from_another_board_are_not_found()
    {
        var client = await factory.CreateSignedInClientAsync("tags-other@example.com");
        var mine = await client.CreateBoardAsync("Tags A");
        var other = await client.CreateBoardAsync("Tags B");
        await CreateCard(client, mine.Id, "Tagged", "ui");
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
        var newOnCard = await client.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "New", tags = new[] { "extra" } });
        var existingOnCard = await client.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Old", tags = new[] { "t1" } });

        Assert.Equal(HttpStatusCode.Conflict, oneMore.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, newOnCard.StatusCode);
        Assert.Contains("\"tags\"", await newOnCard.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, existingOnCard.StatusCode);
    }

    [Fact]
    public async Task Renames_at_the_same_time_as_card_saves_leave_no_card_with_a_tag_the_board_lacks()
    {
        var client = await factory.CreateSignedInClientAsync("tags-race@example.com");
        var board = await client.CreateBoardAsync("Tags race");
        var tagged = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => CreateCard(client, board.Id, $"Tagged {i}", "ui")));
        var plain = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => CreateCard(client, board.Id, $"Plain {i}")));
        var ui = (await TagsAsync(client, board.Id)).Single(t => t.Name == "ui");

        // Each rename reads the tag's name, and each card save checks its tags against the board's, while the others write.
        var renames = Enumerable.Range(0, 6).Select(i => client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{ui.Id}", new { name = $"name-{i}" }));
        var saves = plain.Select(n => client.PatchAsJsonAsync($"/api/cards/{n.Id}", new { tags = new[] { "ui" } }));
        var responses = await Task.WhenAll(renames.Concat(saves));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var names = (await TagsAsync(client, board.Id)).Select(t => t.Name).ToHashSet();
        foreach (var card in tagged.Concat(plain))
        {
            var tags = (await CardAsync(client, card.Id)).Tags;
            Assert.Single(tags);
            Assert.Subset(names, tags.ToHashSet());
        }
    }

    [Fact]
    public async Task Cards_created_at_the_same_time_with_a_new_tag_add_it_once()
    {
        var client = await factory.CreateSignedInClientAsync("tags-new-race@example.com");
        var board = await client.CreateBoardAsync("New tag race");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => CreateCard(client, board.Id, $"Card {i}", "fresh")));

        Assert.Equal([("fresh", 8)], (await TagsAsync(client, board.Id)).Select(t => (t.Name, t.CardCount)));
    }

    private static async Task<CardDetail> CreateCard(HttpClient client, Guid boardId, string title, params string[] tags)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/cards", new { title, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private static async Task<CardDetail> CardAsync(HttpClient client, Guid cardId) =>
        (await client.GetFromJsonAsync<CardDetail>($"/api/cards/{cardId}"))!;

    private static async Task<List<BoardTagDetail>> TagsAsync(HttpClient client, Guid boardId) =>
        (await client.GetFromJsonAsync<List<BoardTagDetail>>($"/api/boards/{boardId}/tags"))!;
}
