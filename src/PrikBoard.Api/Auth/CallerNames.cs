using System.Security.Claims;

namespace PrikBoard.Api.Auth;

public static class CallerNames
{
    /// <summary>
    /// A display name for whoever made the request: a signed-in person's name, or for a machine token its name or
    /// client id (azp: Auth0/Entra, client_id: Okta/Keycloak, appid: Entra v1), falling back to the subject.
    /// </summary>
    public static string DisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(UserPrincipal.NameClaim)
        ?? principal.FindFirstValue("azp")
        ?? principal.FindFirstValue("client_id")
        ?? principal.FindFirstValue("appid")
        ?? principal.FindFirstValue("sub")
        ?? "unknown";
}
