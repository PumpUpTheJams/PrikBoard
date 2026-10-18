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

namespace PrikBoard.Api.Tests;

/// <summary>Bearer access tokens as a machine gets them through Client Credentials, from two trusted issuers.</summary>
public class TokenAuthTests(TokenAuthTests.Factory factory) : IClassFixture<TokenAuthTests.Factory>
{
    private const string Audience = "api://prikboard";

    /// <summary>A stand-in identity provider: an issuer URL and the key it signs with.</summary>
    public record Issuer(string Url, SymmetricSecurityKey Key)
    {
        public static Issuer Create(string url) =>
            new(url, new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    }

    public static readonly Issuer Primary = Issuer.Create("https://primary.idp.test/");
    public static readonly Issuer Secondary = Issuer.Create("https://secondary.idp.test/");
    private static readonly Issuer Untrusted = Issuer.Create("https://untrusted.idp.test/");

    /// <summary>Trusts both issuers, serving their signing keys from memory instead of a discovery document.</summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Tokens:Issuers:primary:Authority", Primary.Url);
            builder.UseSetting("Auth:Tokens:Issuers:secondary:Authority", Secondary.Url);
            builder.ConfigureTestServices(services =>
            {
                foreach (var (id, issuer) in new[] { ("primary", Primary), ("secondary", Secondary) })
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

    [Fact]
    public async Task A_write_token_can_change_and_read_boards()
    {
        var client = Client(Token(scope: "boards:write"));

        (await client.PostAsJsonAsync("/api/boards", new { name = "Made by a machine" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/boards")).StatusCode);
    }

    [Fact]
    public async Task A_read_token_can_only_read()
    {
        var client = Client(Token(scope: "boards:read"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/boards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/boards", new { name = "Nope" })).StatusCode);
    }

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
    public async Task Tokens_from_each_trusted_issuer_are_accepted()
    {
        var me = await Client(Token(Secondary, scope: "boards:read")).GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal((CallerKind.Token, "secondary", "machine-client"), (me!.Kind, me.Provider, me.Name));
        Assert.Equal(["boards:read"], me.Scopes);
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

    private HttpClient Client(string token)
    {
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A Client Credentials style access token: the subject is the machine client, with no user claims.</summary>
    private static string Token(
        Issuer? issuer = null, string? scope = null, string audience = Audience, DateTime? expires = null, Claim[]? extra = null)
    {
        issuer ??= Primary;
        List<Claim> claims = [new("sub", "machine-client@clients"), new("azp", "machine-client"), .. extra ?? []];
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
}
