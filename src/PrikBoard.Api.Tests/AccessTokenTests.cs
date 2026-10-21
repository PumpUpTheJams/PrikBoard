using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Personal access tokens: delegated agents acting on their owner's boards with a capped role.</summary>
public class AccessTokenTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task A_token_is_shown_once_and_only_its_hash_is_stored()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-store@example.com");
        var created = await CreateAsync(owner, "Claude Code", BoardRole.Editor);

        Assert.StartsWith("pb_pat_", created.Token);
        Assert.Equal(56, created.Token.Length);
        Assert.Equal(created.Token[^4..], created.Details.Hint);
        Assert.Equal(DateTimeOffset.UtcNow.AddDays(90).Date, created.Details.ExpiresAt.UtcDateTime.Date);

        var listed = Assert.Single(await owner.GetFromJsonAsync<List<AccessTokenDetail>>("/api/me/tokens") ?? []);
        Assert.Equal(("Claude Code", created.Details.Id), (listed.Name, listed.Id));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var stored = await db.AccessTokens.SingleAsync(t => t.Id == created.Details.Id);
        Assert.Equal(32, stored.TokenHash.Length);
        Assert.DoesNotContain(created.Token[7..20], Convert.ToBase64String(stored.TokenHash));
    }

    [Fact]
    public async Task A_token_works_on_its_owners_boards_as_its_own_agent()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-owner@example.com");
        var board = await owner.CreateBoardAsync("Token board");
        var created = await CreateAsync(owner, "Script", BoardRole.Editor);
        var agent = Bearer(created.Token);

        var me = await agent.MeAsync();
        Assert.Equal((CallerKind.Token, PrincipalKind.Agent, "Script"), (me.Kind, me.PrincipalKind, me.DisplayName));
        Assert.Equal(created.Details.AgentId, me.Id);
        Assert.Equal(["boards:read", "boards:write"], me.Scopes);

        var boards = await agent.GetFromJsonAsync<List<BoardSummary>>("/api/boards");
        var listed = Assert.Single(boards!, b => b.Id == board.Id);
        Assert.Equal(BoardRole.Editor, listed.YourRole);

        var card = await agent.CreateCardAsync(board.Id, "From the token");
        Assert.Equal("From the token", card.Title);

        // Owner-only things stay out of reach, whatever the owner's own role.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_owners_role_caps_the_token_and_read_only_tokens_cannot_write()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-cap-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("pat-cap-viewer@example.com");
        var board = await owner.CreateBoardAsync("Capped");
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        // An Editor token of someone who is only a Viewer stays a Viewer.
        var viewersToken = Bearer((await CreateAsync(viewer, "Viewer's agent", BoardRole.Editor)).Token);
        Assert.Equal(BoardRole.Viewer, (await viewersToken.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.YourRole);
        var refused = await viewersToken.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // A read-only token of the owner can read but not write.
        var readOnly = Bearer((await CreateAsync(owner, "Reader", BoardRole.Viewer)).Token);
        Assert.Equal(["boards:read"], (await readOnly.MeAsync()).Scopes);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"/api/boards/{board.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task A_token_limited_to_boards_sees_only_those()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-limited@example.com");
        var allowed = await owner.CreateBoardAsync("Allowed");
        var other = await owner.CreateBoardAsync("Other");
        var token = Bearer((await CreateAsync(owner, "Limited", BoardRole.Editor, [allowed.Id])).Token);

        var boards = await token.GetFromJsonAsync<List<BoardSummary>>("/api/boards");
        Assert.Equal([allowed.Id], boards!.Select(b => b.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync($"/api/boards/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync("/api/boards", new { name = "New" })).StatusCode);
    }

    [Fact]
    public async Task Tokens_created_by_an_agent_on_your_behalf_make_you_the_owner()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-create@example.com");
        var token = Bearer((await CreateAsync(owner, "Creator", BoardRole.Editor)).Token);

        var response = await token.PostAsJsonAsync("/api/boards", new { name = "Made by the agent" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var board = (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
        Assert.Equal(BoardRole.Editor, board.YourRole);

        var mine = await owner.GetFromJsonAsync<List<BoardSummary>>("/api/boards");
        Assert.Equal(BoardRole.Owner, Assert.Single(mine!, b => b.Id == board.Id).YourRole);
    }

    [Fact]
    public async Task Losing_access_as_a_person_cuts_off_your_tokens()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-lose-owner@example.com");
        var editor = await factory.CreateSignedInClientAsync("pat-lose-editor@example.com");
        var board = await owner.CreateBoardAsync("Shared then not");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        var token = Bearer((await CreateAsync(editor, "Editor's agent", BoardRole.Editor)).Token);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync($"/api/boards/{board.Id}")).StatusCode);

        var editorId = (await editor.MeAsync()).Id;
        await owner.DeleteAsync($"/api/boards/{board.Id}/members/{editorId}");

        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync($"/api/boards/{board.Id}")).StatusCode);
    }

    [Fact]
    public async Task Revoked_expired_and_malformed_tokens_are_refused()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-revoke@example.com");
        var created = await CreateAsync(owner, "Short-lived", BoardRole.Viewer);
        var token = Bearer(created.Token);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/boards")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/me/tokens/{created.Details.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await token.GetAsync("/api/boards")).StatusCode);

        var expiring = await CreateAsync(owner, "Expiring", BoardRole.Viewer);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            await db.AccessTokens.Where(t => t.Id == expiring.Details.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(expiring.Token).GetAsync("/api/boards")).StatusCode);

        var typo = created.Token[..^1] + (created.Token[^1] == 'a' ? 'b' : 'a');
        var response = await Bearer(typo).GetAsync("/api/boards");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Tokens_cannot_manage_tokens_accept_invites_or_look_up_people()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-limits@example.com");
        var board = await owner.CreateBoardAsync("Limits");
        var token = Bearer((await CreateAsync(owner, "Agent", BoardRole.Editor)).Token);

        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/me/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync("/api/me/tokens", new { name = "More", access = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsJsonAsync("/api/invites/accept", new { token = "x" })).StatusCode);
        var lookup = await token.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = "a@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
    }

    [Fact]
    public async Task Creating_checks_access_and_boards()
    {
        var owner = await factory.CreateSignedInClientAsync("pat-validate@example.com");
        var stranger = await factory.CreateSignedInClientAsync("pat-validate-stranger@example.com");
        var strangersBoard = await stranger.CreateBoardAsync("Not yours");

        var asOwner = await owner.PostAsJsonAsync("/api/me/tokens", new { name = "Too much", access = "Owner" });
        Assert.Equal(HttpStatusCode.BadRequest, asOwner.StatusCode);

        var foreign = await owner.PostAsJsonAsync("/api/me/tokens", new { name = "Sneaky", access = "Editor", boardIds = new[] { strangersBoard.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        var tooLong = await owner.PostAsJsonAsync("/api/me/tokens", new { name = "Forever", access = "Viewer", expiresInDays = 400 });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    private static async Task<CreatedAccessToken> CreateAsync(HttpClient owner, string name, BoardRole access, Guid[]? boardIds = null)
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, access, boardIds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedAccessToken>())!;
    }

    private HttpClient Bearer(string token)
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
