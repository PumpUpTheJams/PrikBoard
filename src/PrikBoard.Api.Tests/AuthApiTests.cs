using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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
            // A template that was never filled in.
            builder.UseSetting("Auth:Providers:unfinished:Authority", "https://unfinished.idp.test/");
            // GitHub's side of the sign-in is faked, and the test server speaks http, so the correlation cookie may too.
            builder.ConfigureTestServices(services => services.PostConfigure<OAuthOptions>("github", options =>
            {
                options.Backchannel = new HttpClient(new FakeGitHub());
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            }));
        }
    }

    /// <summary>GitHub's token and user endpoints, for someone PrikBoard doesn't know: GitHub user 4242 without a verified email.</summary>
    private class FakeGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/login/oauth/access_token" => """{"access_token":"fake-token","token_type":"bearer"}""",
                "/user" => """{"id":4242,"login":"newcomer"}""",
                "/user/emails" => "[]",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
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

        // unfinished has no ClientId, so it is left out. github is the default.
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
    public void OpenId_Connect_providers_come_back_by_redirect_so_linking_sees_the_session()
    {
        // form_post would be a cross-site post from the provider, which carries no SameSite=Lax session cookie.
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        Assert.Equal(OpenIdConnectResponseMode.Query, options.Get("acme").ResponseMode);
        Assert.Equal(OpenIdConnectResponseMode.Query, options.Get("google").ResponseMode);
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

    [Fact]
    public async Task Linking_a_sign_in_starts_from_this_site_with_a_session_only()
    {
        var client = await factory.CreateSignedInClientAsync("link-start@example.com");
        var me = await client.MeAsync();

        var linked = await Link(client, "http://localhost");
        Assert.Equal(HttpStatusCode.Redirect, linked.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize", linked.Headers.Location!.ToString());
        var state = StateOf(linked);
        Assert.Equal(me.Id.ToString(), state!.Items[Api.Auth.UserSignIn.LinkUserItem]);
        Assert.Equal("/#/boards/1", state.RedirectUri);

        Assert.Equal(HttpStatusCode.Forbidden, (await Link(client, "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Link(client, origin: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Link(factory.CreateAnonymousClient(), "http://localhost")).StatusCode);
    }

    private static Task<HttpResponseMessage> Link(HttpClient client, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/link/github")
        {
            Content = new FormUrlEncodedContent([new("returnUrl", "/#/boards/1")]),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return client.SendAsync(request);
    }

    private AuthenticationProperties? StateOf(HttpResponseMessage challenge)
    {
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        return factory.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("github").StateDataFormat.Unprotect(state);
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
        Assert.Equal((CallerKind.Session, PrincipalKind.User, "test"), (me!.Kind, me.PrincipalKind, me.Provider));
        // No name is stored: the provider's name is only a suggestion until the person picks one.
        Assert.Equal($"User {me.Id.ToString("N")[^4..]}", me.DisplayName);
        Assert.Null(me.ChosenDisplayName);
        Assert.Equal("ada", me.SuggestedDisplayName);
        Assert.Equal("test", Assert.Single(me.Identities).Provider);
        Assert.Equal(["boards:read", "boards:write"], me.Scopes);

        (await client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_newcomer_refused_while_visiting_a_board_goes_back_to_it()
    {
        // Someone signed up already, so a newcomer needs an invite.
        await factory.CreateSignedInClientAsync("refused-owner@example.com");
        var newcomer = factory.CreateAnonymousClient();

        var challenge = await newcomer.GetAsync("/api/auth/login/github?returnUrl=" + Uri.EscapeDataString("/#/boards/k3x9m2/cards/4"));
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        var callback = await newcomer.GetAsync($"/api/auth/callback/github?code=fake-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/?authError=invite_required#/boards/k3x9m2/cards/4", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await newcomer.GetAsync("/api/me")).StatusCode);
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
        Assert.False(root.GetProperty("paths").TryGetProperty("/api/auth/link/{provider}", out _));
    }

}
