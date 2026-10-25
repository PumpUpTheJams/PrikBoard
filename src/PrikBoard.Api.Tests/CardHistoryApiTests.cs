using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>A card's history: who changed what and when, recorded with every save.</summary>
public class CardHistoryApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _client = await factory.CreateSignedInClientAsync("history@example.com");
        await _client.RenameAsync("Hilde");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_kind_of_change_is_listed_newest_first_with_who_made_it()
    {
        var board = await _client.CreateBoardAsync("History");
        var me = await _client.MeAsync();
        var (toDo, doing) = (board.Buckets[0], board.Buckets[1]);
        var card = await _client.CreateCardAsync(board.Id, "First title");
        // A title given right after creating is part of the Created change, so a component comes first.
        var component = await Ok<CardComponentDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Checklist", title = "Steps" }));
        await Patch(card.Id, new { title = "Second title", tags = new[] { "backend" }, assigneeIds = new[] { me.Id } });
        (await _client.PatchAsJsonAsync($"/api/cards/{card.Id}/components/{component.Id}", new { items = new[] { new { text = "Do it" } } })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { bucketId = doing.Id })).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/cards/{card.Id}/components/{component.Id}")).EnsureSuccessStatusCode();
        var image = await Upload(board.Id, card.Id);
        (await _client.DeleteAsync($"/api/images/{image.Id}")).EnsureSuccessStatusCode();
        var drawing = await Ok<DrawingDetail>(await _client.PostAsJsonAsync($"/api/boards/{board.Id}/drawings",
            new { x = 40, y = 40, points = new[] { new[] { 0.0, 0.0, 0.5 }, new[] { 30.0, 20.0, 0.5 } }, cardId = card.Id }));
        (await _client.DeleteAsync($"/api/drawings/{drawing.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        var deletedHistory = await History(card.Id);
        (await _client.PostAsync($"/api/cards/{card.Id}/restore", null)).EnsureSuccessStatusCode();

        var history = await History(card.Id);

        Assert.Equal(CardChangeKind.Deleted, Assert.Single(deletedHistory[0].Entries).Kind);
        Assert.All(history, c => Assert.Equal("Hilde", c.By!.Name));
        Assert.Equal(history.OrderByDescending(c => c.At).Select(c => c.Id), history.Select(c => c.Id));
        Assert.Equal(
            [
                [CardChangeKind.Restored],
                [CardChangeKind.Deleted],
                [CardChangeKind.DrawingRemoved],
                [CardChangeKind.DrawingAdded],
                [CardChangeKind.ImageRemoved],
                [CardChangeKind.ImageAdded],
                [CardChangeKind.ComponentRemoved],
                [CardChangeKind.Bucket],
                [CardChangeKind.ComponentChanged],
                [CardChangeKind.Title, CardChangeKind.Tags, CardChangeKind.Assignees],
                [CardChangeKind.ComponentAdded],
                [CardChangeKind.Created],
            ],
            history.Select(c => c.Entries.Select(e => e.Kind).ToArray()).ToArray());

        var edits = history[9].Entries;
        Assert.Equal(("First title", "Second title"), (edits[0].Before, edits[0].After));
        Assert.Equal(["backend"], edits[1].Added!);
        Assert.Equal(["Hilde"], edits[2].Added!);
        Assert.Equal(("Checklist", "Steps"), (history[8].Entries[0].Before, history[8].Entries[0].After));
        Assert.Equal((toDo.Name, doing.Name), (history[7].Entries[0].Before, history[7].Entries[0].After));
        Assert.Equal("Steps", history[6].Entries[0].After);
    }

    [Fact]
    public async Task Quick_edits_are_merged_and_a_title_put_back_leaves_nothing()
    {
        var board = await _client.CreateBoardAsync("Merging");
        var card = await _client.CreateCardAsync(board.Id, "Start");
        // Named right after creating it, so part of the Created change.
        await Patch(card.Id, new { title = "Named" });
        (await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Text" })).EnsureSuccessStatusCode();

        await Patch(card.Id, new { title = "One" });
        await Patch(card.Id, new { title = "Two", color = "Mint" });
        var merged = await History(card.Id);
        await Patch(card.Id, new { title = "Named", color = "Butter" });
        var undone = await History(card.Id);

        Assert.Equal(3, merged.Count);
        Assert.Equal(
            [(CardChangeKind.Title, "Named", "Two"), (CardChangeKind.Color, "Butter", "Mint")],
            merged[0].Entries.Select(e => (e.Kind, e.Before, e.After)));
        Assert.Equal([CardChangeKind.ComponentAdded, CardChangeKind.Created], undone.Select(c => c.Entries[0].Kind));
    }

    [Fact]
    public async Task Moving_a_card_within_its_bucket_or_moving_a_component_is_not_a_change()
    {
        var board = await _client.CreateBoardAsync("Quiet moves");
        var card = await _client.CreateCardAsync(board.Id, "Stays put");
        var component = await Ok<CardComponentDetail>(await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Text" }));

        (await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { x = card.X + 20, y = card.Y })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/cards/{card.Id}/components/{component.Id}", new { x = 100, y = 100 })).EnsureSuccessStatusCode();

        Assert.Equal(2, (await History(card.Id)).Count);
    }

    [Fact]
    public async Task A_synchronous_save_is_recorded_too()
    {
        var board = await _client.CreateBoardAsync("Sync save");
        var card = await _client.CreateCardAsync(board.Id);
        var me = await _client.MeAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserPrincipal.IdClaim, me.Id.ToString())], "Test")),
            };
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var stored = db.Cards.Single(n => n.Id == card.Id);
            stored.Tags = ["sync"];
            db.SaveChanges();
            accessor.HttpContext = null;
        }

        var latest = (await History(card.Id))[0];
        Assert.Equal("Hilde", latest.By!.Name);
        Assert.Equal(["sync"], Assert.Single(latest.Entries, e => e.Kind == CardChangeKind.Tags).Added!);
    }

    [Fact]
    public async Task A_kind_this_version_does_not_know_reads_as_other()
    {
        var board = await _client.CreateBoardAsync("Unknown kinds");
        var card = await _client.CreateCardAsync(board.Id);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var entries = """[{"kind":"StickerAdded","after":"star"},{"kind":"Title","before":"A","after":"B"}]""";
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "CardChanges" ("Id", "CardId", "BoardId", "ById", "At", "Entries") VALUES ({Guid.CreateVersion7()}, {card.Id}, {board.Id}, NULL, {DateTimeOffset.UtcNow.AddMinutes(1)}, {entries}::jsonb)""");
        }

        var latest = (await History(card.Id))[0];

        Assert.Null(latest.By);
        Assert.Equal([(CardChangeKind.Other, "star"), (CardChangeKind.Title, "B")], latest.Entries.Select(e => (e.Kind, e.After)));
    }

    [Fact]
    public async Task Viewers_read_the_history_and_strangers_see_nothing()
    {
        var board = await _client.CreateBoardAsync("History roles");
        var card = await _client.CreateCardAsync(board.Id);
        var viewer = await factory.CreateSignedInClientAsync("history-viewer@example.com");
        var stranger = await factory.CreateSignedInClientAsync("history-stranger@example.com");
        await _client.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/cards/{card.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/cards/{card.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cards/{Guid.NewGuid()}/history")).StatusCode);
    }

    private async Task Patch(Guid cardId, object changes) =>
        (await _client.PatchAsJsonAsync($"/api/cards/{cardId}", changes)).EnsureSuccessStatusCode();

    private async Task<List<CardChangeDetail>> History(Guid cardId) =>
        (await _client.GetFromJsonAsync<List<CardChangeDetail>>($"/api/cards/{cardId}/history"))!;

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<ImageDetail> Upload(Guid boardId, Guid cardId)
    {
        var content = new ByteArrayContent(TestImages.Png(300, 200)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };
        return await Ok<ImageDetail>(await _client.PostAsync($"/api/boards/{boardId}/images?cardId={cardId}", content));
    }
}
