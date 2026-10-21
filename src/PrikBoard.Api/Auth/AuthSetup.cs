using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Validators;

using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Wires up authentication. Browsers sign in through an external provider and then hold a session cookie.
/// Machines send an access token from a trusted issuer (usually obtained with Client Credentials) as a Bearer token.
/// A policy scheme picks the cookie or the matching issuer's JWT scheme per request.
/// </summary>
public static partial class AuthSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string SelectorScheme = "CookieOrToken";

    /// <summary>The name of the Bearer security scheme in the OpenAPI document.</summary>
    public const string BearerSecurityScheme = "Bearer";

    /// <summary>Signed in, and for Bearer tokens holding the scope the HTTP method needs (see <see cref="ApiScopes"/>).</summary>
    public const string ApiPolicy = "Api";

    /// <summary>
    /// The MCP endpoint: bearer tokens only (personal access tokens or issuer tokens), never the session cookie, so another
    /// site cannot drive it with a signed-in person's browser. Needs boards:read; write tools check boards:write. A
    /// request with a foreign Origin header gets 403, as the MCP spec requires; agents send none.
    /// </summary>
    public const string McpPolicy = "Mcp";

    /// <summary>Personal access tokens (<c>Bearer pb_pat_...</c>), issued by PrikBoard itself; see <see cref="AccessTokenHandler"/>.</summary>
    public const string AccessTokenScheme = "pat";

    /// <summary>
    /// The live hub: anyone on PrikBoard's own pages (signed in, or a visitor of a public board), or a token with
    /// boards:read or boards:write. Joining a board checks membership, or that the board is public. Not the method-based
    /// API policy, because SignalR's long-polling fallback POSTs even for a read-only token; dragging checks boards:write
    /// itself. Browsers send cookies on cross-site WebSocket upgrades without any CORS check, hence the origin check, which
    /// runs on negotiate and connect alike.
    /// </summary>
    public const string LivePolicy = "Live";

    /// <summary>
    /// The endpoints for the PrikBoard Discord bot: a connected server's <see cref="Domain.AccessTokenKind.Discord"/>
    /// token, which is read-only, also for the POST and DELETE among them. See docs/discord.md.
    /// </summary>
    public const string DiscordPolicy = "Discord";

    /// <summary>
    /// The reads visitors of a public board may make (<see cref="BoardAccess.AllowVisitors"/>): anyone without credentials,
    /// or a signed-in caller with the scope a read needs. Credentials that don't sign in (a wrong token) still get 401.
    /// </summary>
    public const string PublicReadPolicy = "PublicRead";

    public static string CallbackPath(string provider) => $"/api/auth/callback/{provider}";

    // Prefixed so a token issuer and a sign-in provider can share an id such as "auth0".
    private static string TokenScheme(string issuerId) => $"token-{issuerId}";

    public static WebApplicationBuilder AddPrikBoardAuth(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AuthOptions.Section);
        builder.Services.Configure<AuthOptions>(section);
        builder.Services.PostConfigure<AuthOptions>(options =>
        {
            // Unconfigured templates are left out, so the UI only offers providers that work.
            foreach (var id in options.Providers.Where(p => string.IsNullOrWhiteSpace(p.Value.ClientId)).Select(p => p.Key).ToList())
            {
                options.Providers.Remove(id);
            }
        });

        // Resolved (and so validated) at startup in Program.cs; see KeyedHasher.FromConfiguration.
        builder.Services.AddSingleton(services =>
            KeyedHasher.FromConfiguration(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IHostEnvironment>()));
        builder.Services.AddMemoryCache();

        var config = section.Get<AuthOptions>() ?? new AuthOptions();
        var issuers = config.Tokens.Issuers.Where(i => !string.IsNullOrWhiteSpace(i.Value.Authority)).ToList();

        var auth = builder.Services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, null, options =>
                options.ForwardDefaultSelector = context => SelectScheme(context, config.Tokens, issuers))
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "prikboard";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                // This is an API: answer with status codes instead of redirecting to a login page.
                options.Events.OnRedirectToLogin = context => SetStatus(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => SetStatus(context, StatusCodes.Status403Forbidden);
                // A deleted user's cookie stops working (and is cleared) on their next request.
                options.Events.OnValidatePrincipal = async context =>
                {
                    if (Guid.TryParse(context.Principal?.FindFirstValue(UserPrincipal.IdClaim), out var userId)
                        && await UserState.ExistsAsync(context.HttpContext.RequestServices, userId, context.HttpContext.RequestAborted))
                    {
                        return;
                    }
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieScheme);
                };
            })
            .AddScheme<AuthenticationSchemeOptions, AccessTokenHandler>(AccessTokenScheme, null);

        foreach (var (id, provider) in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ClientId)) continue;
            if (!IdPattern().IsMatch(id))
            {
                throw new InvalidOperationException($"Auth provider id '{id}' must use lowercase letters, digits and dashes.");
            }

            switch (provider.Type)
            {
                case ProviderType.OpenIdConnect:
                    AddOpenIdConnect(auth, id, provider);
                    break;
                case ProviderType.GitHub:
                    AddGitHub(auth, id, provider);
                    break;
            }
        }

        foreach (var (id, issuer) in issuers)
        {
            if (!IdPattern().IsMatch(id))
            {
                throw new InvalidOperationException($"Token issuer id '{id}' must use lowercase letters, digits and dashes.");
            }
            if (id is Agent.AccessTokenIssuer or Agent.DiscordIssuer)
            {
                throw new InvalidOperationException($"Token issuer id '{id}' is reserved for PrikBoard's own tokens.");
            }

            AddTokenIssuer(auth, id, issuer, config.Tokens.Audience);
        }

        var apiPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new ApiScopes.Requirement()).Build();
        builder.Services.AddSingleton<IAuthorizationHandler, ApiScopes.Handler>();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(ApiPolicy, apiPolicy)
            .AddPolicy(McpPolicy, policy =>
            {
                policy.AuthenticationSchemes = [AccessTokenScheme, .. issuers.Select(i => TokenScheme(i.Key))];
                policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    context.User.IsToken()
                    && ApiScopes.Of(context.User) is var granted && (granted.Contains(ApiScopes.Read) || granted.Contains(ApiScopes.Write))
                    && (context.Resource is not HttpContext http
                        || string.IsNullOrEmpty(http.Request.Headers.Origin) || SameOrigin.Check(http.Request)));
            })
            .AddPolicy(LivePolicy, policy => policy.RequireAssertion(context =>
                context.User.IsToken()
                    ? ApiScopes.Of(context.User) is var granted && (granted.Contains(ApiScopes.Read) || granted.Contains(ApiScopes.Write))
                    : context.Resource is HttpContext http && !HasCredentials(http.Request, context.User) && SameOrigin.Check(http.Request)))
            .AddPolicy(PublicReadPolicy, policy => policy.RequireAssertion(context =>
                context.User.Identity?.IsAuthenticated == true
                    ? ApiScopes.Of(context.User) is var granted && (granted.Contains(ApiScopes.Read) || granted.Contains(ApiScopes.Write))
                    : context.Resource is not HttpContext http || !HasCredentials(http.Request, context.User)))
            .AddPolicy(DiscordPolicy, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.IsDiscordToken()))
            // Every endpoint without its own authorization metadata gets the API policy.
            .SetFallbackPolicy(apiPolicy);

        return builder;
    }

    /// <summary>
    /// Whether an anonymous request brought a token that didn't sign in (wrong, expired or revoked): it gets 401 rather
    /// than a visitor's view. An empty "Bearer" counts as none, as in <see cref="SelectScheme"/>.
    /// </summary>
    private static bool HasCredentials(HttpRequest request, System.Security.Claims.ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated == true) return false;
        var header = request.Headers.Authorization.ToString().Trim();
        return header.Length > 0 && !header.Equals("Bearer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cookie unless there is a Bearer token. Personal access tokens are recognised by their prefix. An empty "Bearer" header counts as none: Scalar sends one while its token
    /// field is blank, and a signed-in person's cookie should still work then. With several issuers, the token's (not yet validated) iss claim picks the
    /// scheme; that scheme then validates everything, including the issuer. An unknown issuer goes to the first scheme,
    /// which rejects it.
    /// </summary>
    private static string SelectScheme(HttpContext context, TokenOptions tokens, List<KeyValuePair<string, TokenIssuerOptions>> issuers)
    {
        var token = BearerToken(context.Request);
        if (token is not null && AccessTokens.LooksLikeOne(token)) return AccessTokenScheme;
        if (token is null || issuers.Count == 0) return CookieScheme;
        if (issuers.Count == 1) return TokenScheme(issuers[0].Key);

        string? iss = null;
        try
        {
            iss = new JsonWebToken(token).Issuer;
        }
        catch (ArgumentException)
        {
            // Not a JWT; the first scheme will reject it.
        }

        var match = issuers.FirstOrDefault(i => SameIssuer(i.Value.Issuer ?? i.Value.Authority, iss));
        return TokenScheme(match.Key ?? issuers[0].Key);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString().Trim();
        if (!header.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header["Bearer".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static bool SameIssuer(string? configured, string? actual) =>
        configured is not null && actual is not null && configured.TrimEnd('/') == actual.TrimEnd('/');

    private static void AddTokenIssuer(AuthenticationBuilder auth, string id, TokenIssuerOptions issuer, string audience)
    {
        auth.AddJwtBearer(TokenScheme(id), options =>
        {
            options.Authority = issuer.Authority;
            options.Audience = issuer.Audience ?? audience;
            options.MapInboundClaims = false;
            if (issuer.Issuer is not null) options.TokenValidationParameters.ValidIssuer = issuer.Issuer;
            options.Events = new()
            {
                OnTokenValidated = async context =>
                {
                    var identity = (ClaimsIdentity)context.Principal!.Identity!;
                    identity.AddClaim(new Claim(ApiScopes.IssuerIdClaim, id));
                    var services = context.HttpContext.RequestServices;
                    var ct = context.HttpContext.RequestAborted;
                    var clientId = AgentRegistry.ClientId(context.Principal);

                    // An interactive app the issuer lists acts for the person who signed in (their User, token scopes still apply).
                    // Every other client acts as an agent principal, which board owners can add as a member.
                    var subject = context.Principal.FindFirstValue("sub");
                    var principalId = subject is not null && issuer.UserClients.Contains(clientId, StringComparer.Ordinal)
                        ? await UserSignIn.ResolveUserIdAsync(services, issuer.UserProvider ?? id, subject, ct)
                        : await AgentRegistry.ResolveAsync(services, id, clientId, ct);
                    if (principalId is null)
                    {
                        // Only people who joined through an invite in the browser have a user for the app to act as.
                        context.Fail("This person has no PrikBoard account.");
                        return;
                    }
                    identity.AddClaim(new Claim(UserPrincipal.PrincipalClaim, principalId.ToString()));
                },
            };
        });
    }

    private static void AddOpenIdConnect(AuthenticationBuilder auth, string id, ProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Authority))
        {
            throw new InvalidOperationException($"Auth provider '{id}' needs an Authority (the OpenID Connect issuer URL).");
        }

        auth.AddOpenIdConnect(id, provider.DisplayName ?? id, options =>
        {
            options.Authority = provider.Authority;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;
            options.ResponseType = "code";
            // Not form_post (the default): a provider's cross-site post back carries no SameSite=Lax session cookie,
            // so linking a sign-in could not find the session. A redirect with the code in the query does carry it.
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.CallbackPath = CallbackPath(id);
            options.SignInScheme = CookieScheme;
            options.GetClaimsFromUserInfoEndpoint = true;
            // Keep the standard claim names (sub, email, ...) rather than the legacy WS-Federation URIs.
            options.MapInboundClaims = false;
            options.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "email" }.Concat(provider.Scopes).Distinct()) options.Scope.Add(scope);

            // Microsoft's multi-tenant endpoints (common, organizations, consumers) issue tokens whose issuer
            // names the user's own tenant, so the issuer cannot be compared against the metadata as is.
            if (new Uri(provider.Authority).Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                options.TokenValidationParameters.IssuerValidator =
                    AadIssuerValidator.GetAadIssuerValidator(provider.Authority).Validate;
            }

            options.Events.OnTicketReceived = context => UserSignIn.OnTicketReceived(context, id);
            options.Events.OnRemoteFailure = UserSignIn.OnRemoteFailure;
        });
    }

    private static void AddGitHub(AuthenticationBuilder auth, string id, ProviderOptions provider)
    {
        auth.AddOAuth(id, provider.DisplayName ?? "GitHub", options =>
        {
            options.ClientId = provider.ClientId!;
            options.ClientSecret = provider.ClientSecret!;
            options.CallbackPath = CallbackPath(id);
            options.SignInScheme = CookieScheme;
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            foreach (var scope in new[] { "read:user", "user:email" }.Concat(provider.Scopes).Distinct()) options.Scope.Add(scope);

            options.Events.OnCreatingTicket = AddGitHubClaims;
            options.Events.OnTicketReceived = context => UserSignIn.OnTicketReceived(context, id);
            options.Events.OnRemoteFailure = UserSignIn.OnRemoteFailure;
        });
    }

    /// <summary>Maps the GitHub profile to the OpenID Connect claim names that <see cref="UserSignIn"/> expects.</summary>
    private static async Task AddGitHubClaims(OAuthCreatingTicketContext context)
    {
        using var user = await GetGitHubJson(context, context.Options.UserInformationEndpoint);
        var profile = user.RootElement;
        var login = profile.GetProperty("login").GetString();

        // The profile only shows a public email; ask for the primary one and only trust it once GitHub verified it.
        using var emails = await GetGitHubJson(context, "https://api.github.com/user/emails");
        var email = emails.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("primary").GetBoolean() && e.GetProperty("verified").GetBoolean())
            .Select(e => e.GetProperty("email").GetString())
            .FirstOrDefault();

        var identity = context.Identity!;
        identity.AddClaim(new("sub", profile.GetProperty("id").GetInt64().ToString()));
        identity.AddClaim(new("name", profile.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n ? n : login!));
        if (profile.TryGetProperty("avatar_url", out var avatar) && avatar.GetString() is { Length: > 0 } picture)
        {
            identity.AddClaim(new("picture", picture));
        }
        if (email is not null)
        {
            identity.AddClaim(new("email", email));
            // Only GitHub's verified primary address is picked above.
            identity.AddClaim(new("email_verified", "true"));
        }
    }

    private static async Task<JsonDocument> GetGitHubJson(OAuthCreatingTicketContext context, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrikBoard", "1.0"));

        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted));
    }

    private static Task SetStatus(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();
}
