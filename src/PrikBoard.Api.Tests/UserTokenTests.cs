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
/// Tokens an interactive app (the VS Code extension) gets for a signed-in person. The issuer lists the app under
/// <c>UserClients</c>, so the token acts as that person's user, not as an agent.
/// </summary>
public class UserTokenTests(UserTokenTests.Factory factory) : IClassFixture<UserTokenTests.Factory>
{
    private const string ExtensionClient = "vscode-extension";
    private static readonly TokenAuthTests.Issuer Idp = TokenAuthTests.Issuer.Create("https://people.idp.test/");

    /// <summary>The issuer shares its id with the sign-in provider the tests use, so one person has one user.</summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"Auth:Tokens:Issuers:{TestProvider}:Authority", Idp.Url);
            builder.UseSetting($"Auth:Tokens:Issuers:{TestProvider}:UserClients:0", ExtensionClient);
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>($"token-{TestProvider}", options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Idp.Url };
                configuration.SigningKeys.Add(Idp.Key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
        }
    }

    [Fact]
    public async Task A_listed_client_acts_as_the_person_who_signed_in()
    {
        var session = await factory.CreateSignedInClientAsync("sam@example.com", subject: "sam-subject");
        var browser = await session.MeAsync();

        var me = await Client(Token("sam-subject", ExtensionClient, "boards:write")).GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal((CallerKind.Token, PrincipalKind.User, browser.Id), (me!.Kind, me.PrincipalKind, me.Id));
        Assert.Equal(["boards:write"], me.Scopes);
    }

    [Fact]
    public async Task Other_clients_stay_agents_even_with_a_user_subject()
    {
        await factory.CreateSignedInClientAsync("pat@example.com", subject: "pat-subject");

        var me = await Client(Token("pat-subject", "some-other-app", "boards:write")).GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal(PrincipalKind.Agent, me!.PrincipalKind);
        Assert.Equal("some-other-app", me.ClientId);
    }

    [Fact]
    public async Task The_person_keeps_their_boards_roles_and_name_and_the_token_scopes_still_apply()
    {
        var session = await factory.CreateSignedInClientAsync("robin@example.com", subject: "robin-subject");
        (await session.PatchAsJsonAsync("/api/me", new { displayName = "Robin" })).EnsureSuccessStatusCode();
        var board = await session.CreateBoardAsync("Robin's board");
        var write = Client(Token("robin-subject", ExtensionClient, "boards:write"));
        var read = Client(Token("robin-subject", ExtensionClient, "boards:read"));

        var seen = await read.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal(BoardRole.Owner, seen!.YourRole);
        Assert.Equal("Robin", (await write.GetFromJsonAsync<Caller>("/api/me"))!.DisplayName);

        Assert.Equal(HttpStatusCode.Created, (await write.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "From VS Code" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new { title = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task The_app_cannot_create_an_account_for_someone_who_never_joined_in_the_browser()
    {
        var app = Client(Token("newcomer-subject", ExtensionClient, "boards:read"));
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

        await Client(Token("findme-subject", ExtensionClient, "boards:read")).GetAsync("/api/me");

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

    /// <summary>A delegated access token: the subject is the person, and azp the app that asked on their behalf.</summary>
    private static string Token(string subject, string clientId, string scope) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Idp.Url,
            Audience = "api://prikboard",
            Subject = new ClaimsIdentity([new("sub", subject), new("azp", clientId), new("scope", scope)]),
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(Idp.Key, SecurityAlgorithms.HmacSha256),
        });
}
