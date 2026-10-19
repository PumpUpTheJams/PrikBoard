using System.Security.Claims;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The claims in PrikBoard's session cookie, and how to find who a request acts for. The cookie holds no personal data
/// beyond a one-off name suggestion from the sign-in provider, shown to the person themselves until they pick a name.
/// </summary>
public static class UserPrincipal
{
    public const string IdClaim = "sub";
    /// <summary>The sign-in provider used for this session.</summary>
    public const string ProviderClaim = "prikboard_provider";
    /// <summary>The provider's name for the person, offered once as their display name; never stored on the server.</summary>
    public const string SuggestedNameClaim = "prikboard_suggested_name";
    /// <summary>The provider's picture URL, which the person may take as avatar (see <see cref="ProviderAvatars"/>); never stored.</summary>
    public const string SuggestedAvatarClaim = "prikboard_suggested_avatar";
    /// <summary>On token principals: the <see cref="Agent"/> id the token's client maps to.</summary>
    public const string PrincipalClaim = "prikboard_principal";

    public static ClaimsPrincipal Create(
        User user, string provider, string? suggestedName, string authenticationType, string? suggestedAvatar = null)
    {
        List<Claim> claims = [new(IdClaim, user.Id.ToString()), new(ProviderClaim, provider)];
        if (user.DisplayName is null && !string.IsNullOrWhiteSpace(suggestedName))
        {
            claims.Add(new Claim(SuggestedNameClaim, suggestedName.Length <= 100 ? suggestedName : suggestedName[..100]));
        }
        // Kept even once an avatar is taken, so the person can take the provider's current picture again.
        if (ProviderAvatars.IsTrusted(suggestedAvatar) && suggestedAvatar!.Length <= 500)
        {
            claims.Add(new Claim(SuggestedAvatarClaim, suggestedAvatar));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, IdClaim, null));
    }

    /// <summary>The signed-in user's id. Sessions only; see <see cref="PrincipalId"/> for code that also serves tokens.</summary>
    public static Guid UserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(IdClaim)!);

    /// <summary>The user (session) or agent (token) the request acts for.</summary>
    public static Guid PrincipalId(this ClaimsPrincipal principal) =>
        principal.IsToken() ? Guid.Parse(principal.FindFirstValue(PrincipalClaim)!) : principal.UserId();
}
