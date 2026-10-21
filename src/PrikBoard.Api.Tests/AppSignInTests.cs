using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Signing the VS Code extension in: approve in the browser, trade the code with PKCE, get a token that acts as you.</summary>
public class AppSignInTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    private const string OwnOrigin = "http://localhost";

    [Fact]
    public async Task An_app_token_acts_as_the_person_who_approved()
    {
        var person = await factory.CreateSignedInClientAsync("app-person@example.com");
        await person.RenameAsync("Hurles");
        var board = await person.CreateBoardAsync("Mine");
        var self = await person.MeAsync();

        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(person, challenge, "VS Code on laptop", "http://127.0.0.1:53111/callback");
        var exchange = await ExchangeAsync(code.Code, verifier);
        Assert.Equal(HttpStatusCode.Created, exchange.StatusCode);
        var created = (await exchange.Content.ReadFromJsonAsync<CreatedAccessToken>())!;
        Assert.StartsWith("pb_pat_", created.Token);
        Assert.Equal((AccessTokenKind.App, "VS Code on laptop", (Guid?)null), (created.Details.Kind, created.Details.Name, created.Details.AgentId));
        Assert.Equal(DateTimeOffset.UtcNow.AddDays(90).Date, created.Details.ExpiresAt.UtcDateTime.Date);

        var app = Bearer(created.Token);
        var me = await app.MeAsync();
        Assert.Equal((CallerKind.Token, PrincipalKind.User, self.Id, "Hurles"), (me.Kind, me.PrincipalKind, me.Id, me.DisplayName));
        Assert.Equal(["boards:read", "boards:write"], me.Scopes);

        // The person's own role, Owner included, and changes show as them.
        var listed = Assert.Single((await app.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!, b => b.Id == board.Id);
        Assert.Equal(BoardRole.Owner, listed.YourRole);
        var card = await app.CreateCardAsync(board.Id, "From VS Code");
        Assert.Equal(self.Id, card.CreatedById);

        // Still a token: it cannot manage tokens or sign other apps in.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/me/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(app, "/api/me/app-sign-ins", new { name = "x", codeChallenge = challenge }, OwnOrigin)).StatusCode);

        // Listed with the person's tokens, and revoking it stops it at once.
        var token = Assert.Single((await person.GetFromJsonAsync<List<AccessTokenDetail>>("/api/me/tokens"))!, t => t.Id == created.Details.Id);
        Assert.Equal(AccessTokenKind.App, token.Kind);
        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync($"/api/me/tokens/{token.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_code_works_once_and_only_with_its_verifier()
    {
        var person = await factory.CreateSignedInClientAsync("app-once@example.com");

        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(person, challenge);
        Assert.Equal(HttpStatusCode.Created, (await ExchangeAsync(code.Code, verifier)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(code.Code, verifier)).StatusCode);

        // A wrong verifier spends the code too, so it cannot be tried again.
        var (rightVerifier, otherChallenge) = Pkce();
        var other = await ApproveAsync(person, otherChallenge);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(other.Code, Pkce().Verifier)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(other.Code, rightVerifier)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync("not-a-code", verifier)).StatusCode);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var person = await factory.CreateSignedInClientAsync("app-expired@example.com");
        var personId = (await person.MeAsync()).Id;
        var (verifier, challenge) = Pkce();
        var code = await ApproveAsync(person, challenge);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.AppSignInGrants.Where(g => g.OwnerId == personId).ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, past));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(code.Code, verifier)).StatusCode);
    }

    [Fact]
    public async Task Approving_needs_a_session_from_PrikBoards_own_pages_and_a_loopback_redirect()
    {
        var person = await factory.CreateSignedInClientAsync("app-approve@example.com");
        var challenge = Pkce().Challenge;

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(person, "/api/me/app-sign-ins", new { name = "VS Code", codeChallenge = challenge }, "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(person, "/api/me/app-sign-ins", new { name = "VS Code", codeChallenge = challenge }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post(factory.CreateAnonymousClient(), "/api/me/app-sign-ins", new { name = "VS Code", codeChallenge = challenge }, OwnOrigin)).StatusCode);

        var (agent, _) = await factory.TokenAsync(person, "Agent");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(agent, "/api/me/app-sign-ins", new { name = "VS Code", codeChallenge = challenge }, OwnOrigin)).StatusCode);

        foreach (var redirectUri in new[] { "https://evil.example/callback", "http://evil.example:8080/", "http://127.0.0.1/callback", "http://user@127.0.0.1:5000/" })
        {
            var response = await Post(person, "/api/me/app-sign-ins", new { name = "VS Code", codeChallenge = challenge, redirectUri }, OwnOrigin);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var redirectUri in new[] { "http://127.0.0.1:5000/callback", "http://localhost:5000/callback", "http://[::1]:5000/callback" })
        {
            await ApproveAsync(person, Pkce().Challenge, redirectUri: redirectUri);
        }
    }

    [Fact]
    public async Task Only_a_few_sign_ins_can_wait_at_once()
    {
        var person = await factory.CreateSignedInClientAsync("app-pending@example.com");
        for (var i = 0; i < AppSignInGrant.MaxPendingPerOwner; i++) await ApproveAsync(person, Pkce().Challenge);

        var response = await Post(person, "/api/me/app-sign-ins", new { name = "VS Code", codeChallenge = Pkce().Challenge }, OwnOrigin);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static async Task<AppSignInCode> ApproveAsync(HttpClient person, string challenge, string name = "VS Code", string? redirectUri = null)
    {
        var response = await Post(person, "/api/me/app-sign-ins", new { name, codeChallenge = challenge, redirectUri }, OwnOrigin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AppSignInCode>())!;
    }

    private Task<HttpResponseMessage> ExchangeAsync(string code, string codeVerifier) =>
        factory.CreateAnonymousClient().PostAsJsonAsync("/api/auth/app-sign-ins/token", new { code, codeVerifier });

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, object body, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return client.SendAsync(request);
    }

    private HttpClient Bearer(string token)
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
