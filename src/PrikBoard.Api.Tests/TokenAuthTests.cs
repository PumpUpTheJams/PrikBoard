using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// Bearer access tokens as a machine gets them through Client Credentials, from two trusted issuers, and as an interactive
/// app gets them for a signed-in person, from a third.
/// </summary>
public class TokenAuthTests(TokenAuthTests.Factory factory) : IClassFixture<TokenAuthTests.Factory>
{
    private const string Audience = "api://prikboard";
    private const string ExtensionClient = "vscode-extension";

    /// <summary>A stand-in identity provider: an issuer URL and the key it signs with.</summary>
    public record Issuer(string Url, SymmetricSecurityKey Key)
    {
        public static Issuer Create(string url) =>
            new(url, new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    }

    public static readonly Issuer Primary = Issuer.Create("https://primary.idp.test/");
    public static readonly Issuer Secondary = Issuer.Create("https://secondary.idp.test/");
    private static readonly Issuer Untrusted = Issuer.Create("https://untrusted.idp.test/");
    private static readonly Issuer People = Issuer.Create("https://people.idp.test/");

    /// <summary>
    /// Trusts the three issuers, serving their signing keys from memory instead of a discovery document. The people issuer
    /// shares its id with the sign-in provider the tests use, so one person has one user, and lists the extension under
    /// <c>UserClients</c>.
    /// </summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Tokens:Issuers:primary:Authority", Primary.Url);
            builder.UseSetting("Auth:Tokens:Issuers:secondary:Authority", Secondary.Url);
            builder.UseSetting($"Auth:Tokens:Issuers:{TestProvider}:Authority", People.Url);
            builder.UseSetting($"Auth:Tokens:Issuers:{TestProvider}:UserClients:0", ExtensionClient);
            builder.ConfigureTestServices(services =>
            {
                foreach (var (id, issuer) in new[] { ("primary", Primary), ("secondary", Secondary), (TestProvider, People) })
                {
                    services.PostConfigure<JwtBearerOptions>($"token-{id}", options =>
                    {
                        var configuration = new OpenIdConnectConfiguration { Issuer = issuer.Url };
                        configuration.SigningKeys.Add(issuer.Key);
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });
                }
            });
        }
    }

    // What read and write tokens may do is in Agents_reach_a_board_once_an_owner_adds_them_within_their_scopes.
    [Fact]
    public async Task A_token_without_board_scopes_is_forbidden()
    {
        var response = await Client(Token(scope: "openid profile")).GetAsync("/api/boards");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Entra_style_roles_and_scp_claims_grant_scopes_too()
    {
        var roles = Client(Token(extra: [new("roles", "boards:write")]));
        var scp = Client(Token(extra: [new("scp", "boards:read")]));

        (await roles.PostAsJsonAsync("/api/boards", new { name = "Via app role" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await scp.GetAsync("/api/boards")).StatusCode);
    }

    [Fact]
    public async Task An_empty_bearer_header_falls_back_to_the_session_cookie()
    {
        // Scalar sends "Authorization: Bearer" while its token field is blank.
        var client = await factory.CreateSignedInClientAsync();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");

        var me = await client.GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal(CallerKind.Session, me!.Kind);
    }

    [Fact]
    public async Task Untrusted_wrong_audience_and_expired_tokens_are_rejected()
    {
        string[] tokens =
        [
            Token(Untrusted, scope: "boards:write"),
            // Signed by a trusted key but claiming to come from somewhere else.
            Token(Primary with { Url = Untrusted.Url }, scope: "boards:write"),
            Token(scope: "boards:write", audience: "api://someone-else"),
            Token(scope: "boards:write", expires: DateTime.UtcNow.AddMinutes(-10)),
            "not-a-jwt",
        ];

        foreach (var token in tokens)
        {
            var response = await Client(token).GetAsync("/api/boards");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.First().Scheme);
        }
    }

    [Fact]
    public async Task Tokens_from_each_trusted_issuer_are_accepted_and_each_client_is_one_stable_agent_per_issuer()
    {
        var first = await Client(Token(scope: "boards:read")).GetFromJsonAsync<Caller>("/api/me");
        var second = await Client(Token(scope: "boards:write")).GetFromJsonAsync<Caller>("/api/me");
        var other = await Client(Token(Secondary, scope: "boards:read")).GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal((CallerKind.Token, PrincipalKind.Agent, "secondary", "machine-client"), (other!.Kind, other.PrincipalKind, other.Provider, other.ClientId));
        Assert.Equal("Agent machine-client", other.DisplayName);
        Assert.Equal(["boards:read"], other.Scopes);
        Assert.Equal(first!.Id, second!.Id);
        Assert.NotEqual(first.Id, other.Id); // same client id, different issuer
    }

    [Fact]
    public async Task Agents_reach_a_board_once_an_owner_adds_them_within_their_scopes()
    {
        var owner = await factory.CreateSignedInClientAsync("agent-owner@example.com");
        var board = await owner.CreateBoardAsync("For the agent");
        var agent = Client(Token(scope: "boards:write", clientId: "helper-bot"));
        var reader = Client(Token(scope: "boards:read", clientId: "helper-bot"));

        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/boards/{board.Id}")).StatusCode);

        var added = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/agents",
            new { issuer = "primary", clientId = "helper-bot", role = "Editor", displayName = "Helper" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var member = (await added.Content.ReadFromJsonAsync<BoardMemberDetail>())!;
        Assert.Equal((PrincipalKind.Agent, "Helper", "primary", "helper-bot"), (member.Kind, member.DisplayName, member.Issuer, member.ClientId));

        Assert.Equal(HttpStatusCode.Created, (await agent.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "From the agent" })).StatusCode);
        // A read token stays read-only whatever the role.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Nope" })).StatusCode);

        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{member.PrincipalId}", new { role = "Viewer" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/boards/{board.Id}")).StatusCode);

        var unknownIssuer = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/agents", new { issuer = "elsewhere", clientId = "x", role = "Viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownIssuer.StatusCode);
    }

    [Fact]
    public async Task Agents_own_the_boards_they_create_but_cannot_use_invites_or_search_people()
    {
        var agent = Client(Token(scope: "boards:write", clientId: "builder-bot"));
        var created = await agent.PostAsJsonAsync("/api/boards", new { name = "Agent's own" });
        var board = (await created.Content.ReadFromJsonAsync<BoardDetail>())!;
        Assert.Equal(BoardRole.Owner, board.YourRole);

        var lookup = await agent.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = "someone@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
        var invite = await (await agent.PostAsJsonAsync($"/api/boards/{board.Id}/invites", new { role = "Viewer" })).Content.ReadFromJsonAsync<CreatedInvite>();
        var otherAgent = Client(Token(scope: "boards:write", clientId: "sneaky-bot"));
        Assert.Equal(HttpStatusCode.Forbidden, (await otherAgent.PostAsJsonAsync("/api/invites/accept", new { token = invite!.Token })).StatusCode);

        var renamed = await agent.PatchAsJsonAsync("/api/me", new { displayName = "Builder" });
        Assert.Equal("Builder", (await renamed.Content.ReadFromJsonAsync<Caller>())!.DisplayName);
    }

    // Tokens an interactive app (the VS Code extension) gets for a signed-in person. The people issuer lists the app under
    // UserClients, so the token acts as that person's user, not as an agent.
    [Fact]
    public async Task Other_clients_stay_agents_even_with_a_user_subject()
    {
        await factory.CreateSignedInClientAsync("pat@example.com", subject: "pat-subject");

        var me = await Client(DelegatedToken("pat-subject", "some-other-app", "boards:write")).GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal(PrincipalKind.Agent, me!.PrincipalKind);
        Assert.Equal("some-other-app", me.ClientId);
    }

    [Fact]
    public async Task A_listed_client_acts_as_the_person_with_their_boards_roles_and_name_within_the_token_scopes()
    {
        var session = await factory.CreateSignedInClientAsync("robin@example.com", subject: "robin-subject");
        (await session.PatchAsJsonAsync("/api/me", new { displayName = "Robin" })).EnsureSuccessStatusCode();
        var browser = await session.MeAsync();
        var board = await session.CreateBoardAsync("Robin's board");
        var write = Client(DelegatedToken("robin-subject", ExtensionClient, "boards:write"));
        var read = Client(DelegatedToken("robin-subject", ExtensionClient, "boards:read"));

        var me = await write.GetFromJsonAsync<Caller>("/api/me");
        Assert.Equal((CallerKind.Token, PrincipalKind.User, browser.Id, "Robin"), (me!.Kind, me.PrincipalKind, me.Id, me.DisplayName));
        Assert.Equal(["boards:write"], me.Scopes);
        var seen = await read.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal(BoardRole.Owner, seen!.YourRole);

        Assert.Equal(HttpStatusCode.Created, (await write.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "From VS Code" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task The_app_cannot_create_an_account_for_someone_who_never_joined_in_the_browser()
    {
        var app = Client(DelegatedToken("newcomer-subject", ExtensionClient, "boards:read"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/me")).StatusCode);

        var browser = await (await factory.CreateSignedInClientAsync("newcomer@example.com", subject: "newcomer-subject")).MeAsync();

        Assert.Equal(browser.Id, (await app.GetFromJsonAsync<Caller>("/api/me"))!.Id);
    }

    [Fact]
    public async Task Using_a_token_does_not_clear_the_email_the_person_can_be_found_by()
    {
        var owner = await factory.CreateSignedInClientAsync("searcher@example.com");
        var findable = await factory.CreateSignedInClientAsync("findme@example.com", subject: "findme-subject");
        var board = await owner.CreateBoardAsync("Lookup after token");

        await Client(DelegatedToken("findme-subject", ExtensionClient, "boards:read")).GetAsync("/api/me");

        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = "findme@example.com" });
        var hits = await response.Content.ReadFromJsonAsync<List<UserMatch>>();
        Assert.Equal((await findable.MeAsync()).Id, Assert.Single(hits!).PrincipalId);
    }

    private HttpClient Client(string token)
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A Client Credentials style access token: the subject is the machine client, with no user claims.</summary>
    private static string Token(
        Issuer? issuer = null, string? scope = null, string audience = Audience, DateTime? expires = null, Claim[]? extra = null,
        string clientId = "machine-client")
    {
        issuer ??= Primary;
        List<Claim> claims = [new("sub", $"{clientId}@clients"), new("azp", clientId), .. extra ?? []];
        if (scope is not null) claims.Add(new Claim("scope", scope));

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer.Url,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = expiry.AddMinutes(-20),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(issuer.Key, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>A delegated access token: the subject is the person, and azp the app that asked on their behalf.</summary>
    private static string DelegatedToken(string subject, string clientId, string scope) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = People.Url,
            Audience = "api://prikboard",
            Subject = new ClaimsIdentity([new("sub", subject), new("azp", clientId), new("scope", scope)]),
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(People.Key, SecurityAlgorithms.HmacSha256),
        });
}
