using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapGet("/providers", ListAuthProviders)
            .AllowAnonymous()
            .WithName("listAuthProviders")
            .WithSummary("List sign-in providers")
            .WithDescription("Returns the identity providers people can sign in with in the browser, the default first. " +
                             "Machines do not sign in this way; they send an access token from a trusted issuer as a Bearer token.");

        // A browser navigation, not an API call, so it stays out of the OpenAPI document.
        auth.MapGet("/login/{provider}", Login)
            .AllowAnonymous()
            .ExcludeFromDescription();

        auth.MapPost("/logout", Logout)
            .AllowAnonymous()
            .WithName("logout")
            .WithSummary("Sign out")
            .WithDescription("Ends the browser session by clearing the session cookie. Bearer tokens are not affected.");

        api.MapGet("/me", WhoAmI)
            .WithTags("Auth")
            .WithName("whoAmI")
            .WithSummary("Get the caller")
            .WithDescription("Returns who the request is authenticated as and its scopes. Useful to check that an access token works.");

        return api;
    }

    private static Ok<List<AuthProvider>> ListAuthProviders(IOptions<AuthOptions> options)
    {
        // Configuration binding sorts the providers by id, so the default is put first explicitly.
        var providers = options.Value.Providers
            .OrderByDescending(p => string.Equals(p.Key, options.Value.DefaultProvider, StringComparison.OrdinalIgnoreCase))
            .Select(p => new AuthProvider(p.Key, p.Value.DisplayName ?? p.Key))
            .ToList();
        return TypedResults.Ok(providers);
    }

    /// <summary>Starts sign-in with a provider, which redirects back to the local returnUrl afterwards.</summary>
    private static Results<ChallengeHttpResult, NotFound> Login(string provider, string? returnUrl, IOptions<AuthOptions> options)
    {
        if (!options.Value.Providers.ContainsKey(provider)) return TypedResults.NotFound();

        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = target }, [provider]);
    }

    private static SignOutHttpResult Logout() => TypedResults.SignOut(authenticationSchemes: [AuthSetup.CookieScheme]);

    private static async Task<Results<Ok<Caller>, UnauthorizedHttpResult>> WhoAmI(
        ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var scopes = ApiScopes.Of(principal).Where(s => s is ApiScopes.Read or ApiScopes.Write).Order().ToList();

        if (principal.IsToken())
        {
            // Machine tokens usually carry no name; DisplayName falls back to the client id.
            var subject = principal.FindFirstValue("sub") ?? "";
            return TypedResults.Ok(new Caller(CallerKind.Token, subject, principal.DisplayName(), null,
                principal.FindFirstValue(ApiScopes.IssuerIdClaim)!, scopes));
        }

        var user = await db.Users.FindAsync([principal.UserId()], ct);
        return user is null
            ? TypedResults.Unauthorized()
            : TypedResults.Ok(new Caller(CallerKind.Session, user.Id.ToString(), user.Name, user.Email, user.Provider, scopes));
    }

    /// <summary>Only same-site paths, so a crafted login link cannot bounce people to another site.</summary>
    private static bool IsLocalUrl(string? url) =>
        url is ['/', ..] && !url.StartsWith("//") && !url.StartsWith("/\\") && !url.Any(char.IsControl);
}
