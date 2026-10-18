using System.Security.Claims;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>The claims PrikBoard puts in its own session cookie and API key principals.</summary>
public static class UserPrincipal
{
    public const string IdClaim = "sub";
    public const string NameClaim = "name";
    public const string EmailClaim = "email";

    public static ClaimsPrincipal Create(User user, string authenticationType)
    {
        List<Claim> claims = [new(IdClaim, user.Id.ToString()), new(NameClaim, user.Name)];
        if (user.Email is not null) claims.Add(new Claim(EmailClaim, user.Email));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, NameClaim, null));
    }

    public static Guid UserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(IdClaim)!);
}
