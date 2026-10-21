using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Public boards: anyone with the link reads them, signed in or not, but tokens still need membership.</summary>
public class PublicBoardApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task A_private_board_is_not_found_by_anyone_but_its_members()
    {
        var owner = await factory.CreateSignedInClientAsync("public-private-owner@example.com");
        var stranger = await factory.CreateSignedInClientAsync("public-private-stranger@example.com");
        var board = await owner.CreateBoardAsync("Private");
        var anonymous = factory.CreateAnonymousClient();

        Assert.False(board.IsPublic);
        foreach (var client in new[] { anonymous, stranger })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.ShortId}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/boards/{board.ShortId}")).StatusCode);
    }

    [Fact]
    public async Task Anyone_reads_a_public_board_without_the_people_on_it()
    {
        var owner = await factory.CreateSignedInClientAsync("public-read-owner@example.com");
        var board = await owner.CreateBoardAsync("Docs");
        var me = await owner.MeAsync();
        var card = await Created<CardDetail>(await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Download", assigneeIds = new[] { me.Id } }));
        var image = await Created<ImageDetail>(await owner.PostAsync($"/api/boards/{board.Id}/images",
            new ByteArrayContent(TestImages.Png(10, 10)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }));
        var made = await Ok<BoardSummary>(await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true, rules = "Read me." }));
        Assert.Equal((true, false), (made.IsPublic, made.PublicShowsPeople));

        var anonymous = factory.CreateAnonymousClient();
        var stranger = await factory.CreateSignedInClientAsync("public-read-stranger@example.com");
        foreach (var client in new[] { anonymous, stranger })
        {
            var seen = (await client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.ShortId}"))!;
            Assert.Equal((board.Id, true, BoardRole.Viewer, "Read me."), (seen.Id, seen.Visiting, seen.YourRole, seen.Rules));
            Assert.Empty(seen.People);
            var shown = seen.Cards.Single(n => n.Id == card.Id);
            Assert.Equal(new Assignee(Guid.Empty, "", AssigneeKind.Person, null), Assert.Single(shown.Assignees));
            Assert.Equal((null, null), (shown.CreatedBy, shown.CreatedById));
            Assert.Equal(("", null), (seen.Images.Single().AddedBy, seen.Images.Single().AddedById));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.ContentUrl)).StatusCode);
            // Nobody's picture, while the board hides people.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/boards/{board.Id}/avatars/{me.Id}")).StatusCode);
        }

        // Members still see everything, and aren't visiting.
        var mine = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal((false, me.Id), (mine.Visiting, Assert.Single(mine.Cards.Single(n => n.Id == card.Id).Assignees).PrincipalId));
    }

    [Fact]
    public async Task A_public_board_can_show_its_people_to_visitors()
    {
        var owner = await factory.CreateSignedInClientAsync("public-people-owner@example.com");
        var board = await owner.CreateBoardAsync("People");
        var me = await owner.MeAsync();
        await Created<CardDetail>(await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Mine", assigneeIds = new[] { me.Id } }));
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true, publicShowsPeople = true })).EnsureSuccessStatusCode();

        var seen = (await factory.CreateAnonymousClient().GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;

        var card = Assert.Single(seen.Cards);
        Assert.Equal((me.Id, me.DisplayName), (Assert.Single(card.Assignees).PrincipalId, card.Assignees[0].Name));
        Assert.Equal(me.DisplayName, card.CreatedBy);
        Assert.Empty(seen.People);
    }

    [Fact]
    public async Task Visitors_only_read_the_board_its_images_and_drawings()
    {
        var owner = await factory.CreateSignedInClientAsync("public-write-owner@example.com");
        var board = await owner.CreateBoardAsync("Read only");
        var card = await owner.CreateCardAsync(board.Id);
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();
        var anonymous = factory.CreateAnonymousClient();
        var stranger = await factory.CreateSignedInClientAsync("public-write-stranger@example.com");

        // Writes need a caller, then membership; other reads (the trash, an export, one card) are for members.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = false })).StatusCode);
        foreach (var url in new[] { $"/api/boards/{board.Id}/trash", $"/api/boards/{board.Id}/export", $"/api/cards/{card.Id}", $"/api/boards/{board.Id}/members" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }
        // The board doesn't join the stranger's list.
        Assert.DoesNotContain((await stranger.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!, b => b.Id == board.Id);
    }

    [Fact]
    public async Task Tokens_need_membership_and_a_wrong_token_is_refused()
    {
        var owner = await factory.CreateSignedInClientAsync("public-token-owner@example.com");
        var stranger = await factory.CreateSignedInClientAsync("public-token-stranger@example.com");
        var board = await owner.CreateBoardAsync("Tokens");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();
        var (strangerToken, _) = await factory.TokenAsync(stranger, "stranger's agent");
        var (ownerToken, _) = await factory.TokenAsync(owner, "owner's agent");
        var wrong = factory.CreateAnonymousClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pb_pat_not-a-token");

        Assert.Equal(HttpStatusCode.NotFound, (await strangerToken.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.False((await ownerToken.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Visiting);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_owners_make_a_board_public()
    {
        var owner = await factory.CreateSignedInClientAsync("public-owner-only@example.com");
        var editor = await factory.CreateSignedInClientAsync("public-editor-only@example.com");
        var board = await owner.CreateBoardAsync("Owners");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).StatusCode);
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateAnonymousClient().GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
