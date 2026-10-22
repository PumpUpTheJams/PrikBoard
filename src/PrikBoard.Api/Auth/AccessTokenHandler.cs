using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer pb_pat_...</c>. The principal carries the same claims as an issuer token (so
/// scopes and <c>PrincipalId()</c> work unchanged). An agent token adds the owner, maximum role and boards that
/// <see cref="BoardCaller"/> applies; an app token is the owner themselves; a Discord token is its server's agent, read-only.
/// Tokens found are cached for a minute; revoking evicts at once on this instance. Unknown tokens are not cached (anyone can
/// make up well-formed ones): <see cref="AccessTokenLookups"/> limits how many one address may try instead.
/// </summary>
public sealed class AccessTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    PrikBoardDbContext db, IMemoryCache cache, AccessTokenLookups lookups)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary>Set when the address tried too many unknown tokens: the challenge answers 429 with this Retry-After.</summary>
    private const string RetryAfterItem = "PrikBoard.AccessTokens.RetryAfter";

    private sealed record Found(Guid Id, AccessTokenKind Kind, Guid OwnerId, Guid? AgentId, BoardRole MaxRole, List<Guid>? BoardIds, DateTimeOffset ExpiresAt);

    /// <summary>Drops a cached lookup, so a revoked token stops working at once.</summary>
    public static void Forget(IMemoryCache cache, byte[] tokenHash) => cache.Remove(Key(tokenHash));

    private static string Key(byte[] tokenHash) => "pat:" + Convert.ToHexString(tokenHash);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (!AccessTokens.LooksLikeOne(token)) return AuthenticateResult.NoResult();
        if (!AccessTokens.IsWellFormed(token)) return AuthenticateResult.Fail("Malformed access token.");

        var hash = AccessTokens.Hash(token);
        var key = Key(hash);
        if (!cache.TryGetValue(key, out Found? found) || found is null)
        {
            if (!lookups.MayLookUp(Context, out var retryAfter))
            {
                Context.Items[RetryAfterItem] = retryAfter;
                return AuthenticateResult.Fail("Too many unknown access tokens from this address.");
            }
            found = await db.AccessTokens.AsNoTracking()
                .Where(t => t.TokenHash == hash)
                .Select(t => new Found(t.Id, t.Kind, t.OwnerId, t.AgentId, t.MaxRole, t.BoardIds, t.ExpiresAt))
                .FirstOrDefaultAsync(Context.RequestAborted);
            if (found is null)
            {
                lookups.Unknown(Context);
                return AuthenticateResult.Fail("Unknown or revoked access token.");
            }
            cache.Set(key, found, CacheFor);

            // Once per cache period at most, which is all "last used" needs.
            var now = DateTimeOffset.UtcNow;
            await db.AccessTokens.Where(t => t.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
        }

        if (found.ExpiresAt <= DateTimeOffset.UtcNow) return AuthenticateResult.Fail("The access token has expired.");

        List<Claim> claims = [new(ApiScopes.IssuerIdClaim, Agent.AccessTokenIssuer), new("client_id", found.Id.ToString())];
        if (found.Kind == AccessTokenKind.App)
        {
            // The owner themselves, like a token from an issuer's UserClients: no cap, so BoardCaller uses their own roles.
            claims.Add(new Claim(UserPrincipal.PrincipalClaim, found.OwnerId.ToString()));
            claims.Add(new Claim("scope", $"{ApiScopes.Read} {ApiScopes.Write}"));
        }
        else if (found.Kind == AccessTokenKind.Discord)
        {
            // A Discord server's agent: read-only, with its own memberships (no owner cap), so the owner who connected it
            // leaving a board doesn't break it; removing the server's membership does.
            claims.Add(new Claim(UserPrincipal.PrincipalClaim, found.AgentId!.Value.ToString()));
            claims.Add(new Claim("scope", ApiScopes.Read));
            claims.Add(new Claim(AccessTokens.KindClaim, nameof(AccessTokenKind.Discord)));
        }
        else
        {
            claims.Add(new Claim(UserPrincipal.PrincipalClaim, found.AgentId!.Value.ToString()));
            claims.Add(new Claim("scope", AccessTokens.ScopesFor(found.MaxRole)));
            claims.Add(new Claim(AccessTokens.OwnerClaim, found.OwnerId.ToString()));
            claims.Add(new Claim(AccessTokens.MaxRoleClaim, found.MaxRole.ToString()));
            if (found.BoardIds is { } boards) claims.Add(new Claim(AccessTokens.BoardsClaim, string.Join(' ', boards)));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Not 401: the token may well be valid, and a client should wait rather than throw it away.
        if (Context.Items[RetryAfterItem] is TimeSpan retryAfter)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }
}
