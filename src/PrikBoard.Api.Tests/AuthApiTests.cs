using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Contracts;

namespace PrikBoard.Api.Tests;

public class AuthApiTests(AuthApiTests.Factory factory) : IClassFixture<AuthApiTests.Factory>
{
    /// <summary>
    /// Configures GitHub (the default, and it needs no discovery document to start a sign-in) plus two OpenID Connect
    /// providers that are only listed, never contacted.
    /// </summary>
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Providers:github:ClientId", "test-client");
            builder.UseSetting("Auth:Providers:github:ClientSecret", "test-secret");
            // Sorts before github, so the list shows that the default goes first rather than alphabetical order.
            builder.UseSetting("Auth:Providers:acme:DisplayName", "Acme SSO");
            builder.UseSetting("Auth:Providers:acme:Authority", "https://acme.idp.test/");
            builder.UseSetting("Auth:Providers:acme:ClientId", "acme-client");
            builder.UseSetting("Auth:Providers:google:ClientId", "google-client");
        }
    }

    [Theory]
    [InlineData("/api/boards")]
    [InlineData("/api/me")]
    public async Task Api_requires_a_signed_in_user(string url)
    {
        var response = await factory.CreateAnonymousClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/health")]
    [InlineData("/api/auth/providers")]
    public async Task Contract_health_and_providers_are_public(string url)
    {
        var response = await factory.CreateAnonymousClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Providers_list_only_configured_providers_with_the_default_first()
    {
        var providers = await factory.CreateAnonymousClient().GetFromJsonAsync<List<AuthProvider>>("/api/auth/providers");

        // microsoft is a template in appsettings.json without a ClientId, so it is left out. github is the default.
        Assert.Equal([new AuthProvider("github", "GitHub"), new AuthProvider("acme", "Acme SSO"), new AuthProvider("google", "Google")], providers);
    }

    [Fact]
    public async Task Login_redirects_to_the_provider_with_a_callback_under_the_api()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/github?returnUrl=/%23/boards/1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("https://github.com/login/oauth/authorize", location.GetLeftPart(UriPartial.Path));
        Assert.Contains("client_id=test-client", location.Query);
        Assert.Contains(Uri.EscapeDataString("/api/auth/callback/github"), location.Query);
    }

    [Fact]
    public async Task The_api_reference_sends_people_through_the_default_provider_first()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/scalar");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/api/auth/login/github?returnUrl=%2Fscalar", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Signed_in_people_get_the_api_reference()
    {
        var client = await factory.CreateSignedInClientAsync();

        // Scalar itself redirects /scalar to /scalar/.
        var response = await client.GetAsync("/scalar/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_with_an_unknown_provider_is_not_found()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/api/auth/login/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/#/boards/42", "/#/boards/42")]
    public async Task Login_only_returns_to_local_urls(string returnUrl, string expected)
    {
        var response = await factory.CreateAnonymousClient()
            .GetAsync($"/api/auth/login/github?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, ReturnUrlOf(response));
    }

    /// <summary>The address the user returns to after GitHub, which travels encrypted in the OAuth state parameter.</summary>
    private string? ReturnUrlOf(HttpResponseMessage challenge)
    {
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("github");
        return options.StateDataFormat.Unprotect(state)?.RedirectUri;
    }

    [Fact]
    public async Task A_session_identifies_the_user_until_logout()
    {
        var client = await factory.CreateSignedInClientAsync("ada@example.com");

        var me = await client.GetFromJsonAsync<Caller>("/api/me");
        Assert.Equal((CallerKind.Session, "ada", "ada@example.com", "test"), (me!.Kind, me.Name, me.Email, me.Provider));
        Assert.Equal(["boards:read", "boards:write"], me.Scopes);

        (await client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Signing_in_again_finds_the_same_user()
    {
        var first = await (await factory.CreateSignedInClientAsync("same@example.com")).GetFromJsonAsync<Caller>("/api/me");
        var second = await (await factory.CreateSignedInClientAsync("same@example.com")).GetFromJsonAsync<Caller>("/api/me");

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task OpenApi_document_declares_the_bearer_scheme_and_public_operations()
    {
        using var document = JsonDocument.Parse(await factory.CreateAnonymousClient().GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal(("http", "bearer"), (scheme.GetProperty("type").GetString(), scheme.GetProperty("scheme").GetString()));
        Assert.True(root.GetProperty("security")[0].TryGetProperty("Bearer", out _));

        var listProviders = root.GetProperty("paths").GetProperty("/api/auth/providers").GetProperty("get");
        Assert.Equal(0, listProviders.GetProperty("security").GetArrayLength());
        Assert.False(root.GetProperty("paths").TryGetProperty("/api/auth/login/{provider}", out _));
    }

}

public class AllowlistTests(AllowlistTests.Factory factory) : IClassFixture<AllowlistTests.Factory>
{
    public class Factory : PrikBoardApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:AllowedEmailDomains:0", "example.com");
            builder.UseSetting("Auth:AllowedEmails:0", "guest@partner.test");
        }
    }

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("GUEST@partner.test")]
    public async Task Allowed_emails_and_domains_can_sign_in(string email)
    {
        var client = await factory.CreateSignedInClientAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Other_emails_get_no_session()
    {
        var client = factory.CreateAnonymousClient();

        var response = await PrikBoardApiFactory.TestSignInAsync(client, "intruder@elsewhere.test");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }
}
