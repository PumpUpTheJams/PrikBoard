using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Deleted cards, buckets, images and tags can be restored with the same id (what undo in the web app uses).</summary>
public class RestoreApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_deleted_card_is_hidden_everywhere_and_comes_back_whole_with_its_images()
    {
        var board = await _client.CreateBoardAsync("Restore a card");
        var created = await CreateCard(board.Id, new { title = "Ship the beta", tags = new[] { "release" }, components = new[] { new { kind = "Text", text = "Checklist for launch" } } });
        var card = await Card(created.Id); // as stored, with CreatedAt in microseconds
        var attached = await Upload(board.Id, $"?cardId={card.Id}");
        var removedEarlier = await Upload(board.Id, $"?cardId={card.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/images/{removedEarlier.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/cards/{card.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cards/{card.Id}")).StatusCode);
        var hidden = await Board(board.Id);
        Assert.Empty(hidden.Cards);
        Assert.Empty(hidden.Images);
        Assert.Equal(0, Assert.Single(hidden.Tags).CardCount);
        Assert.Empty((await _client.GetFromJsonAsync<List<CardDetail>>($"/api/boards/{board.Id}/cards?search=launch"))!);
        Assert.DoesNotContain("Ship the beta", await _client.GetStringAsync($"/api/boards/{board.Id}/export"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(attached.ContentUrl)).StatusCode);
        // Its images come back with the card, not on their own.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/images/{attached.Id}/restore", null)).StatusCode);

        var response = await _client.PostAsync($"/api/cards/{card.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var restored = (await response.Content.ReadFromJsonAsync<CardDetail>())!;

        Assert.Equal((card.Id, card.CreatedById, card.CreatedAt, card.X, card.Y), (restored.Id, restored.CreatedById, restored.CreatedAt, restored.X, restored.Y));
        Assert.Equal(card.BucketId, restored.BucketId);
        Assert.Equal(["release"], restored.Tags);
        Assert.Equal(card.Components.Single().Id, Assert.Single(restored.Components).Id);
        var back = await Board(board.Id);
        Assert.Equal(card.Id, Assert.Single(back.Cards).Id);
        Assert.Equal(attached.Id, Assert.Single(back.Images).Id); // the image deleted on its own earlier stays deleted
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(attached.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/cards/{card.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_card_with_nothing_on_it_is_deleted_for_good_and_others_go_to_the_trash()
    {
        var board = await _client.CreateBoardAsync("Blank cards");
        var blank = await _client.CreateCardAsync(board.Id, title: "  ");
        // A component without text doesn't count, as in the web app.
        var emptyComponent = await CreateCard(board.Id, new { title = "", components = new[] { new { kind = "Checklist" } } });
        var titled = await _client.CreateCardAsync(board.Id, title: "Keep me");
        var tagged = await CreateCard(board.Id, new { title = "", tags = new[] { "later" } });
        var withImage = await _client.CreateCardAsync(board.Id, title: "");
        var image = await Upload(board.Id, $"?cardId={withImage.Id}");
        // An image removed from it on its own could still come back onto it.
        var imageGone = await _client.CreateCardAsync(board.Id, title: "");
        var removed = await Upload(board.Id, $"?cardId={imageGone.Id}");
        (await _client.DeleteAsync($"/api/images/{removed.Id}")).EnsureSuccessStatusCode();

        foreach (var card in new[] { blank, emptyComponent, titled, tagged, withImage, imageGone })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/cards/{card.Id}")).StatusCode);
        }

        var trash = (await _client.GetFromJsonAsync<TrashContents>($"/api/boards/{board.Id}/trash"))!;
        Assert.Equal(
            new[] { titled.Id, tagged.Id, withImage.Id, imageGone.Id }.Order(),
            trash.Cards.Select(n => n.Id).Order());
        Assert.Equal(image.Id, Assert.Single(trash.Images).Id);
        foreach (var card in new[] { blank, emptyComponent })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/cards/{card.Id}/restore", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/cards/{card.Id}/history")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/cards/{titled.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_restored_card_joins_the_bucket_at_its_place_now()
    {
        var board = await _client.CreateBoardAsync("Restore into a bucket");
        var toDo = board.Buckets[0];
        var card = await _client.CreateCardAsync(board.Id);
        Assert.Equal(toDo.Id, card.BucketId);

        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = 4000, y = 4000 })).EnsureSuccessStatusCode();
        var restored = await Restore<CardDetail>($"/api/cards/{card.Id}/restore");

        Assert.Equal((card.X, card.Y), (restored.X, restored.Y)); // a deleted card is not carried along
        Assert.Null(restored.BucketId);
    }

    [Fact]
    public async Task A_restored_bucket_keeps_its_place_in_the_stack_and_takes_its_cards_back()
    {
        var board = await _client.CreateBoardAsync("Restore a bucket");
        var toDo = board.Buckets[0];
        var card = await _client.CreateCardAsync(board.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/buckets/{toDo.Id}")).StatusCode);
        Assert.Null((await Board(board.Id)).Cards.Single().BucketId);
        var restored = await Restore<BucketDetail>($"/api/buckets/{toDo.Id}/restore");

        Assert.Equal((toDo.Name, toDo.X, toDo.Y, toDo.Width, toDo.Height), (restored.Name, restored.X, restored.Y, restored.Width, restored.Height));
        var back = await Board(board.Id);
        Assert.Equal(board.Buckets.Select(b => b.Id), back.Buckets.Select(b => b.Id));
        Assert.Equal(toDo.Id, back.Cards.Single(n => n.Id == card.Id).BucketId);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/buckets/{toDo.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_restored_tag_goes_back_on_the_cards_it_was_removed_from_even_deleted_ones()
    {
        var board = await _client.CreateBoardAsync("Restore a tag");
        var live = await CreateCard(board.Id, new { title = "Live", tags = new[] { "urgent", "api" } });
        var deleted = await CreateCard(board.Id, new { title = "Deleted", tags = new[] { "urgent" } });
        var untagged = await CreateCard(board.Id, new { title = "Untagged" });
        var tag = (await Board(board.Id)).Tags.Single(t => t.Name == "urgent");
        (await _client.DeleteAsync($"/api/cards/{deleted.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/boards/{board.Id}/tags/{tag.Id}")).StatusCode);
        Assert.Equal(["api"], (await Card(live.Id)).Tags);
        var restored = await Restore<BoardTagDetail>($"/api/boards/{board.Id}/tags/{tag.Id}/restore");

        Assert.Equal((tag.Id, "urgent", 1), (restored.Id, restored.Name, restored.CardCount));
        Assert.Equal(["api", "urgent"], (await Card(live.Id)).Tags);
        Assert.Empty((await Card(untagged.Id)).Tags);
        Assert.Equal(["urgent"], (await Restore<CardDetail>($"/api/cards/{deleted.Id}/restore")).Tags);
    }

    [Fact]
    public async Task A_deleted_tag_name_can_be_used_again_and_then_the_old_tag_cannot_come_back()
    {
        var board = await _client.CreateBoardAsync("Tag name reused");
        await CreateCard(board.Id, new { title = "Tagged", tags = new[] { "design" } });
        var old = (await Board(board.Id)).Tags.Single();
        (await _client.DeleteAsync($"/api/boards/{board.Id}/tags/{old.Id}")).EnsureSuccessStatusCode();

        var again = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "design" });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.NotEqual(old.Id, (await again.Content.ReadFromJsonAsync<BoardTagDetail>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/boards/{board.Id}/tags/{old.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Renaming_a_tag_reaches_deleted_cards_too()
    {
        var board = await _client.CreateBoardAsync("Rename while deleted");
        var card = await CreateCard(board.Id, new { title = "Old tag", tags = new[] { "old" } });
        var tag = (await Board(board.Id)).Tags.Single();
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();

        (await _client.PatchAsJsonAsync($"/api/boards/{board.Id}/tags/{tag.Id}", new { name = "new" })).EnsureSuccessStatusCode();

        Assert.Equal(["new"], (await Restore<CardDetail>($"/api/cards/{card.Id}/restore")).Tags);
    }

    [Fact]
    public async Task Deleted_things_cannot_be_changed_and_unknown_ones_cannot_be_restored()
    {
        var board = await _client.CreateBoardAsync("Gone");
        var card = await _client.CreateCardAsync(board.Id);
        var image = await Upload(board.Id);
        var bucket = board.Buckets[0];
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/images/{image.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/buckets/{bucket.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/cards/{card.Id}", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/cards/{card.Id}/move", new { x = 0, y = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { kind = "Text" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/cards/{card.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { x = 0, y = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsJsonAsync($"/api/buckets/{bucket.Id}", new { name = "x" })).StatusCode);

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/cards/{unknown}/restore", null)).StatusCode);
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
        var card = await _client.CreateCardAsync(board.Id);
        (await _client.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/cards/{card.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/cards/{card.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Things_deleted_longer_ago_than_the_retention_are_purged_for_good()
    {
        var board = await _client.CreateBoardAsync("Purge");
        var old = await _client.CreateCardAsync(board.Id, "Old");
        var recent = await _client.CreateCardAsync(board.Id, "Recent");
        var image = await Upload(board.Id, $"?cardId={old.Id}");
        (await _client.DeleteAsync($"/api/cards/{old.Id}")).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/cards/{recent.Id}")).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var longAgo = DateTimeOffset.UtcNow - Tombstones.Retention - TimeSpan.FromDays(1);
            await db.Cards.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).Where(n => n.Id == old.Id)
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

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/cards/{old.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/cards/{recent.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_bucket_move_with_carry_card_ids_takes_exactly_those_cards_along()
    {
        var board = await _client.CreateBoardAsync("Carry");
        var other = await _client.CreateBoardAsync("Elsewhere");
        var toDo = board.Buckets[0];
        var carried = await _client.CreateCardAsync(board.Id, "Carried");
        var stays = await _client.CreateCardAsync(board.Id, "Stays");
        var foreign = await _client.CreateCardAsync(other.Id, "Foreign");

        var moved = await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = toDo.X + 2000, carryCardIds = new[] { carried.Id, foreign.Id } });
        var resize = await _client.PatchAsJsonAsync($"/api/buckets/{toDo.Id}", new { x = 0, width = 400, carryCardIds = new[] { carried.Id } });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal((carried.X + 2000, toDo.Id), ((await Card(carried.Id)).X, (await Card(carried.Id)).BucketId));
        Assert.Equal((stays.X, (Guid?)null), ((await Card(stays.Id)).X, (await Card(stays.Id)).BucketId));
        Assert.Equal(foreign.X, (await Card(foreign.Id)).X);
        Assert.Equal(HttpStatusCode.BadRequest, resize.StatusCode);
    }

    [Fact]
    public async Task A_removed_component_can_be_put_back_with_its_id()
    {
        var board = await _client.CreateBoardAsync("Component back");
        var card = await CreateCard(board.Id, new { title = "Components", components = new[] { new { kind = "Text", text = "Keep me" } } });
        var component = card.Components.Single();
        (await _client.DeleteAsync($"/api/cards/{card.Id}/components/{component.Id}")).EnsureSuccessStatusCode();

        var back = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components",
            new { id = component.Id, kind = "Text", text = component.Text, x = component.X, y = component.Y, width = component.Width, height = component.Height });
        var twice = await _client.PostAsJsonAsync($"/api/cards/{card.Id}/components", new { id = component.Id, kind = "Text" });

        Assert.Equal(HttpStatusCode.Created, back.StatusCode);
        var restored = (await back.Content.ReadFromJsonAsync<CardComponentDetail>())!;
        Assert.Equal((component.Id, component.Text, component.X, component.Y, component.Width, component.Height),
            (restored.Id, restored.Text, restored.X, restored.Y, restored.Width, restored.Height));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    private async Task<CardDetail> CreateCard(Guid boardId, object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/boards/{boardId}/cards", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }

    private async Task<BoardDetail> Board(Guid boardId) => (await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{boardId}"))!;

    private async Task<CardDetail> Card(Guid cardId) => (await _client.GetFromJsonAsync<CardDetail>($"/api/cards/{cardId}"))!;

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
