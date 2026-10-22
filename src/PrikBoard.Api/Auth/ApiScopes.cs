using System.Collections.Frozen;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Scopes that limit what a Bearer token may do. Reads (GET, HEAD) need boards:read or boards:write,
/// everything else needs boards:write. Browser sessions are people and have full access.
/// </summary>
public static class ApiScopes
{
    public const string Read = "boards:read";
    public const string Write = "boards:write";

    /// <summary>Added to token principals, naming the configured issuer that vouched for the token.</summary>
    public const string IssuerIdClaim = "prikboard_issuer";

    public static bool IsToken(this ClaimsPrincipal principal) => principal.HasClaim(c => c.Type == IssuerIdClaim);

    /// <summary>
    /// Scopes granted to a principal. Providers put them in different claims: "scope" (Auth0, Okta, Keycloak),
    /// "scp" (Entra delegated) or "roles" (Entra application permissions, used with Client Credentials).
    /// </summary>
    public static IReadOnlySet<string> Of(ClaimsPrincipal principal)
    {
        if (!principal.IsToken()) return Everything;

        return principal.Claims
            .Where(IsScopeClaim)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether the principal was granted <paramref name="scope"/>, like <see cref="Of"/> but without building the set (every request asks).</summary>
    public static bool Has(ClaimsPrincipal principal, string scope)
    {
        if (!principal.IsToken()) return Everything.Contains(scope);
        foreach (var claim in principal.Claims)
        {
            if (!IsScopeClaim(claim)) continue;
            var value = claim.Value.AsSpan();
            foreach (var range in value.Split(' '))
            {
                if (value[range].SequenceEqual(scope)) return true;
            }
        }
        return false;
    }

    /// <summary>Whether the principal may read at all (boards:read or boards:write).</summary>
    public static bool CanRead(ClaimsPrincipal principal) => Has(principal, Read) || Has(principal, Write);

    private static readonly FrozenSet<string> Everything = new[] { Read, Write }.ToFrozenSet(StringComparer.Ordinal);

    private static bool IsScopeClaim(Claim claim) => claim.Type is "scope" or "scp" or "roles";

    public class Requirement : IAuthorizationRequirement;

    public class Handler : AuthorizationHandler<Requirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, Requirement requirement)
        {
            var method = (context.Resource as HttpContext)?.Request.Method ?? "";
            var allowed = Has(context.User, Write) || (Has(context.User, Read) && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)));
            if (allowed) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
}
