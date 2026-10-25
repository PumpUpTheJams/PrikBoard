using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class CardComponentApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task New_components_get_their_default_size_and_a_free_spot_next_to_each_other()
    {
        var card = await NewCard();

        var text = await Add(card.Id, new { kind = "Text", text = "Retry tiles", title = "  Context " });
        var checklist = await Add(card.Id, new { kind = "Checklist" });
        var links = await Add(card.Id, new { kind = "Links" });

        Assert.Equal((20, 20, 340, 220, "Context", "Retry tiles"), (text.X, text.Y, text.Width, text.Height, text.Title, text.Text));
        Assert.Equal((380, 20, 340, 260), (checklist.X, checklist.Y, checklist.Width, checklist.Height));
        // 740 + 360 still fits the 1100 px row.
        Assert.Equal((740, 20, 360, 220), (links.X, links.Y, links.Width, links.Height));
        Assert.Null(checklist.Text);
        Assert.Empty(checklist.Items!);
        Assert.Null(checklist.Links);

        var fourth = await Add(card.Id, new { kind = "Text" });
        Assert.Equal((20, 260), (fourth.X, fourth.Y));
    }

    [Fact]
    public async Task Components_move_and_resize_on_the_grid_within_limits()
    {
        var card = await NewCard();
        var component = await Add(card.Id, new { kind = "Text", x = 131, y = 289, width = 401, height = 50 });
        Assert.Equal((140, 280, 400, 80), (component.X, component.Y, component.Width, component.Height));

        var moved = await Update(card.Id, component.Id, new { x = 10_000, y = 9, width = 150 });

        Assert.Equal((10_000 - 160, 0, 160, 80), (moved.X, moved.Y, moved.Width, moved.Height));
    }

    [Fact]
    public async Task Content_must_fit_the_kind_and_can_be_cleared()
    {
        var card = await NewCard();
        var text = await Add(card.Id, new { kind = "Text", text = "Draft", title = "Cards" });
        var url = $"/api/cards/{card.Id}/components/{text.Id}";

        var items = await _client.PatchAsJsonAsync(url, new { items = new[] { new { text = "x" } } });
        var noKind = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { text = "x" });
        var xOnly = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Text", x = 40 });
        var cleared = await Update(card.Id, text.Id, new { text = "", title = " " });

        Assert.Equal(HttpStatusCode.BadRequest, items.StatusCode);
        Assert.Contains("items", await items.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, xOnly.StatusCode);
        Assert.Equal(("", null), (cleared.Text, cleared.Title));
    }

    [Fact]
    public async Task Components_are_deleted_and_unknown_ones_are_not_found()
    {
        var card = await NewCard();
        var other = await NewCard();
        var component = await Add(card.Id, new { kind = "Links" });

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/cards/{other.Id}/components/{component.Id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/cards/{card.Id}/components/{component.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/cards/{card.Id}/components/{component.Id}")).StatusCode);
        Assert.Empty((await Get(card.Id)).Components);
    }

    [Fact]
    public async Task A_card_holds_at_most_thirty_components()
    {
        var card = await NewCard();
        for (var i = 0; i < CardComponent.MaxComponents; i++) await Add(card.Id, new { kind = "Text" });

        var response = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Text" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Edits_to_different_components_at_the_same_time_both_survive()
    {
        var card = await NewCard();
        var components = new List<CardComponentDetail>();
        for (var i = 0; i < 6; i++) components.Add(await Add(card.Id, new { kind = "Text" }));

        await Task.WhenAll(components.Select((b, i) => _client.PatchAsJsonAsync($"/api/cards/{card.Id}/components/{b.Id}", new { text = $"Edit {i}" })));

        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"Edit {i}"), (await Get(card.Id)).Components.Select(b => b.Text));
    }

    private async Task<CardDetail> NewCard()
    {
        var board = await _client.CreateBoardAsync("Components");
        return await _client.CreateCardAsync(board.Id);
    }

    private async Task<CardDetail> Get(Guid cardId) => (await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{cardId}"))!;

    private async Task<CardComponentDetail> Add(Guid cardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/cards/{cardId}/components", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardComponentDetail>())!;
    }

    private async Task<CardComponentDetail> Update(Guid cardId, Guid componentId, object request)
    {
        var response = await _client.PatchAsJsonAsync($"/api/cards/{cardId}/components/{componentId}", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardComponentDetail>())!;
    }
}
