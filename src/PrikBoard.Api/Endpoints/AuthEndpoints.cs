using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth").NoBoardScope();

        auth.MapGet("/providers", ListAuthProviders)
            .AllowAnonymous()
            .WithName("listAuthProviders")
            .WithSummary("List sign-in providers")
            .WithDescription("Returns the identity providers people can sign in with in the browser, the default first. " +
                             "Machines do not sign in this way; they send an access token from a trusted issuer as a Bearer token.");

        auth.MapGet("/token-issuers", ListTokenIssuers)
            .WithName("listTokenIssuers")
            .WithSummary("List trusted token issuers")
            .WithDescription("Returns the ids of the issuers whose access tokens the API accepts; needed to add an agent to a board.");

        // A browser navigation, not an API call, so it stays out of the OpenAPI document.
        auth.MapGet("/login/{provider}", Login)
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Browser form post: adds another sign-in to the current user. See Link for why a form is safe here.
        auth.MapPost("/link/{provider}", Link)
            .ExcludeFromDescription();

        auth.MapPost("/logout", Logout)
            .AllowAnonymous()
            .WithName("logout")
            .WithSummary("Sign out")
            .WithDescription("Ends the browser session by clearing the session cookie. Bearer tokens are not affected.");

        auth.MapGet("/version", GetVersion)
            .WithName("getVersion")
            .WithSummary("Get the server version")
            .WithDescription("Returns the version of the running server, so people and agents can tell which build they talk to.");

        var me = api.MapGroup("/me").WithTags("Auth").NoBoardScope();

        me.MapGet("/", WhoAmI)
            .WithName("whoAmI")
            .WithSummary("Get the caller")
            .WithDescription("Returns who the request is authenticated as: a person (session) or an agent (token), its principal id, " +
                             "display name and scopes. Agents share their id (or issuer and client id) with a board owner to be added.");

        me.MapPatch("/", UpdateMe)
            .WithName("updateMe")
            .WithSummary("Change your display name")
            .WithDescription("Sets the name others see on boards (at most 100 characters); an empty string clears it.")
            .ProducesValidationProblem();

        me.MapPut("/avatar", UseSignInAvatar)
            .WithName("useSignInAvatar")
            .WithSummary("Take your sign-in picture as avatar")
            .WithDescription("Copies the picture of the provider you signed in with (suggestedAvatarUrl in whoAmI; GitHub only) " +
                             "and shows it instead of your initials. GitHub sign-ins do this by themselves unless you removed your " +
                             "avatar; this undoes that. People in the browser only.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        me.MapDelete("/avatar", RemoveAvatar)
            .WithName("removeAvatar")
            .WithSummary("Remove your avatar")
            .WithDescription("Deletes your picture, so your initials are shown again, and stops later sign-ins from taking it " +
                             "(avatarOptedOut). People in the browser only.");

        me.MapDelete("/identities/{identityId:guid}", UnlinkIdentity)
            .WithName("unlinkIdentity")
            .WithSummary("Unlink a sign-in")
            .WithDescription("Removes one of your linked sign-ins. The last one cannot be removed. People only.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/avatars/{principalId:guid}", GetAvatar)
            .WithTags("Auth")
            .NoBoardScope()
            .WithName("getAvatar")
            .WithSummary("Download a person's avatar")
            .WithDescription("Returns the picture a person chose (avatarUrl on people in other answers). The v query parameter " +
                             "changes with each new picture, so an answer can be cached.");

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

    private static Ok<ServerVersion> GetVersion() => TypedResults.Ok(new ServerVersion(AppVersion.Current));

    private static Ok<List<TokenIssuer>> ListTokenIssuers(IOptions<AuthOptions> options) =>
        TypedResults.Ok(options.Value.Tokens.Issuers
            .Where(i => !string.IsNullOrWhiteSpace(i.Value.Authority))
            .Select(i => new TokenIssuer(i.Key))
            .OrderBy(i => i.Id)
            .ToList());

    /// <summary>Starts sign-in with a provider, which redirects back to the local returnUrl afterwards.</summary>
    private static Results<ChallengeHttpResult, NotFound> Login(string provider, string? returnUrl, IOptions<AuthOptions> options)
    {
        if (!options.Value.Providers.ContainsKey(provider)) return TypedResults.NotFound();

        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = target }, [provider]);
    }

    /// <summary>
    /// Starts a provider round trip whose result is added to the signed-in user (see UserSignIn's link branch).
    /// It is a form post because the browser must navigate to the provider. Another site cannot start it with someone's
    /// session: the cookie is SameSite=Lax (not sent on cross-site posts) and the Origin must be this site. The user id
    /// travels in the encrypted OAuth state and must match the session again on the way back.
    /// </summary>
    private static async Task<Results<ChallengeHttpResult, NotFound, ForbidHttpResult>> Link(
        string provider, HttpContext http, ClaimsPrincipal principal, IOptions<AuthOptions> options)
    {
        if (principal.IsToken() || !SameOrigin.Check(http.Request)) return TypedResults.Forbid();
        if (!options.Value.Providers.ContainsKey(provider)) return TypedResults.NotFound();

        // Read by hand rather than bound with [FromForm], which would require antiforgery tokens the SPA does not use.
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
        var returnUrl = form?["returnUrl"].ToString();
        var properties = new AuthenticationProperties { RedirectUri = IsLocalUrl(returnUrl) ? returnUrl! : "/" };
        properties.Items[UserSignIn.LinkUserItem] = principal.UserId().ToString();
        return TypedResults.Challenge(properties, [provider]);
    }

    private static SignOutHttpResult Logout() => TypedResults.SignOut(authenticationSchemes: [AuthSetup.CookieScheme]);

    private static async Task<Results<Ok<Caller>, UnauthorizedHttpResult>> WhoAmI(
        ClaimsPrincipal principal, PrikBoardDbContext db, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var caller = await CallerOf(principal, db, options.Value, ct);
        return caller is null ? TypedResults.Unauthorized() : TypedResults.Ok(caller);
    }

    private static async Task<Results<Ok<Caller>, UnauthorizedHttpResult>> UpdateMe(
        UpdateMeRequest request, ClaimsPrincipal principal, HttpContext http, PrikBoardDbContext db,
        IOptions<AuthOptions> options, BoardEvents events, CancellationToken ct)
    {
        var self = await db.Principals.FindAsync([principal.PrincipalId()], ct);
        if (self is null) return TypedResults.Unauthorized();

        if (request.DisplayName is not null)
        {
            var name = new string(request.DisplayName.Where(c => !char.IsControl(c)).ToArray()).Trim();
            self.DisplayName = name.Length == 0 ? null : name;
        }
        await db.SaveChangesAsync(ct);
        await events.ProfileChangedAsync(self);
        if (request.DisplayName is not null) await events.ProfileShownOnBoardsAsync(db, http, ct);

        if (!principal.IsToken() && self is User user)
        {
            // Re-issue the cookie without the provider's name suggestion: the person has now decided.
            principal = UserPrincipal.Create(user, principal.FindFirstValue(UserPrincipal.ProviderClaim) ?? "", null,
                AuthSetup.CookieScheme, principal.FindFirstValue(UserPrincipal.SuggestedAvatarClaim));
            await http.SignInAsync(AuthSetup.CookieScheme, principal);
        }

        return TypedResults.Ok((await CallerOf(principal, db, options.Value, ct))!);
    }

    private static async Task<Results<Ok<Caller>, UnauthorizedHttpResult, ForbidHttpResult, ProblemHttpResult>> UseSignInAvatar(
        ClaimsPrincipal principal, HttpContext http, PrikBoardDbContext db, ProviderAvatars pictures,
        IOptions<AuthOptions> options, BoardEvents events, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var picture = principal.FindFirstValue(UserPrincipal.SuggestedAvatarClaim);
        if (picture is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No picture",
                detail: "The provider you signed in with offered no picture. Sign in with GitHub to take yours.");
        }

        var user = await db.Users.FindAsync([principal.UserId()], ct);
        if (user is null) return TypedResults.Unauthorized();
        if (!await pictures.TakeAsync(db, user, picture, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Picture unavailable",
                detail: "Your picture could not be fetched from the provider. Try again later.");
        }

        await AvatarChangedAsync(user, db, http, events, ct);
        return TypedResults.Ok((await CallerOf(principal, db, options.Value, ct))!);
    }

    private static async Task<Results<Ok<Caller>, UnauthorizedHttpResult, ForbidHttpResult>> RemoveAvatar(
        ClaimsPrincipal principal, HttpContext http, PrikBoardDbContext db, IOptions<AuthOptions> options, BoardEvents events,
        CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var user = await db.Users.FindAsync([principal.UserId()], ct);
        if (user is null) return TypedResults.Unauthorized();

        var had = user.AvatarUpdatedAt is not null;
        // The file goes at the next sweep, once nothing refers to it.
        user.AvatarFileId = null;
        user.AvatarUpdatedAt = null;
        user.AvatarOptedOut = true;
        await db.SaveChangesAsync(ct);
        if (had) await AvatarChangedAsync(user, db, http, events, ct);
        return TypedResults.Ok((await CallerOf(principal, db, options.Value, ct))!);
    }

    /// <summary>Presence and every board the person can reach show the new picture.</summary>
    private static async Task AvatarChangedAsync(User user, PrikBoardDbContext db, HttpContext http, BoardEvents events, CancellationToken ct)
    {
        await events.ProfileChangedAsync(user);
        await events.ProfileShownOnBoardsAsync(db, http, ct);
    }

    private static async Task<Results<FileContentHttpResult, PhysicalFileHttpResult, NotFound>> GetAvatar(
        Guid principalId, HttpResponse response, PrikBoardDbContext db, FileStore files, CancellationToken ct)
    {
        var file = await db.Users.AsNoTracking().Where(u => u.Id == principalId).Select(u => u.AvatarFile).FirstOrDefaultAsync(ct);
        if (file is null) return TypedResults.NotFound();
        // The URL is versioned (avatarUrl's v), so caching it for good is safe.
        return await StoredFileResults.ServeAsync(file, files, response, ct);
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ProblemHttpResult>> UnlinkIdentity(
        Guid identityId, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var userId = principal.UserId();

        var identities = await db.UserIdentities.Where(i => i.UserId == userId).ToListAsync(ct);
        var identity = identities.Find(i => i.Id == identityId);
        if (identity is null) return TypedResults.NotFound();
        if (identities.Count == 1)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Last sign-in",
                detail: "Link another sign-in before removing this one, or you could not sign in again.");
        }

        db.UserIdentities.Remove(identity);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The caller as the API describes it; null when a session points at a user that no longer exists.</summary>
    internal static async Task<Caller?> CallerOf(ClaimsPrincipal principal, PrikBoardDbContext db, AuthOptions options, CancellationToken ct)
    {
        var scopes = ApiScopes.Of(principal).Where(s => s is ApiScopes.Read or ApiScopes.Write).Order().ToList();

        if (principal.IsToken())
        {
            var issuer = principal.FindFirstValue(ApiScopes.IssuerIdClaim);
            switch (await db.Principals.FindAsync([principal.PrincipalId()], ct))
            {
                case Agent agent:
                    return new Caller(CallerKind.Token, PrincipalKind.Agent, agent.Id, agent.ShownName, agent.DisplayName, null,
                        agent.Issuer, agent.ClientId, [], scopes, false, null, null, false);
                // A token from one of the issuer's UserClients: the person themselves, limited to the token's scopes.
                // Sign-ins are managed in the browser, so none are listed.
                case User person:
                    return new Caller(CallerKind.Token, PrincipalKind.User, person.Id, person.ShownName, person.DisplayName, null,
                        issuer, AgentRegistry.ClientId(principal), [], scopes, false, Avatars.UrlOf(person), null, person.AvatarOptedOut);
                default:
                    return null;
            }
        }

        var user = await db.Users.Include(u => u.Identities).FirstOrDefaultAsync(u => u.Id == principal.UserId(), ct);
        if (user is null) return null;

        var identities = user.Identities
            .OrderBy(i => i.LinkedAt)
            .Select(i => new LinkedIdentity(i.Id, i.Provider,
                options.Providers.TryGetValue(i.Provider, out var p) ? p.DisplayName ?? i.Provider : i.Provider, i.LinkedAt))
            .ToList();
        var suggested = user.DisplayName is null ? principal.FindFirstValue(UserPrincipal.SuggestedNameClaim) : null;
        return new Caller(CallerKind.Session, PrincipalKind.User, user.Id, user.ShownName, user.DisplayName, suggested,
            principal.FindFirstValue(UserPrincipal.ProviderClaim), null, identities, scopes, user.IsAdmin,
            Avatars.UrlOf(user), principal.FindFirstValue(UserPrincipal.SuggestedAvatarClaim), user.AvatarOptedOut);
    }

    /// <summary>Only same-site paths, so a crafted login link cannot bounce people to another site.</summary>
    private static bool IsLocalUrl(string? url) =>
        url is ['/', ..] && !url.StartsWith("//") && !url.StartsWith("/\\") && !url.Any(char.IsControl);
}
