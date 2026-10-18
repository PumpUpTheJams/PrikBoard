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
        if (!principal.IsToken()) return new HashSet<string> { Read, Write };

        return principal.Claims
            .Where(c => c.Type is "scope" or "scp" or "roles")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    public class Requirement : IAuthorizationRequirement;

    public class Handler : AuthorizationHandler<Requirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, Requirement requirement)
        {
            var method = (context.Resource as HttpContext)?.Request.Method;
            var granted = Of(context.User);
            var allowed = granted.Contains(Write) || (granted.Contains(Read) && (HttpMethods.IsGet(method ?? "") || HttpMethods.IsHead(method ?? "")));
            if (allowed) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
}
