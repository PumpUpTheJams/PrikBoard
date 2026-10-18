using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Turns an identity from an external provider into a PrikBoard user and session, or links it to the signed-in user.
/// Only keyed hashes of the provider's subject id and verified email are stored; names and emails pass through.
/// </summary>
public static class UserSignIn
{
    public const string NotAllowedError = "not_allowed";
    public const string FailedError = "failed";
    public const string LinkFailedError = "link_failed";
    public const string IdentityInUseError = "identity_in_use";

    /// <summary>
    /// Set on the challenge of a link flow (see <c>AuthEndpoints.Link</c>): the id of the user the new sign-in should join.
    /// The properties travel encrypted in the OAuth state, so they cannot be forged.
    /// </summary>
    public const string LinkUserItem = "prikboard_link_user";

    public enum LinkResult
    {
        Linked,
        AlreadyLinked,
        InUseByAnotherUser,
        NotAllowed,
        UnknownUser,
    }

    /// <summary>
    /// Finds or creates the user for a provider identity and returns the principal for the session cookie, or null when
    /// the allowlist rejects <paramref name="email"/>. <paramref name="verifiedEmail"/> (only when the provider vouches
    /// for it) is hashed for exact-email search. Never links identities by email: that would let a provider with weak
    /// email checks take over accounts.
    /// </summary>
    public static async Task<ClaimsPrincipal?> SignInAsync(
        HttpContext context, string provider, string subject, string? name, string? email, string? verifiedEmail)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!options.Allows(email)) return null;

        var db = context.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var hasher = context.RequestServices.GetRequiredService<KeyedHasher>();
        var subjectHash = hasher.Subject(provider, subject);

        var identity = await db.UserIdentities.Include(i => i.User)
            .FirstOrDefaultAsync(i => i.Provider == provider && i.SubjectHash == subjectHash, context.RequestAborted);
        if (identity is null)
        {
            identity = new UserIdentity { User = new User(), Provider = provider, SubjectHash = subjectHash };
            db.UserIdentities.Add(identity);
        }

        // Emails change at the provider; keep the hash current so search finds the address the person uses now.
        identity.EmailHash = verifiedEmail is null ? null : hasher.Email(verifiedEmail);
        identity.User!.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.RequestAborted);

        return UserPrincipal.Create(identity.User, provider, name, AuthSetup.CookieScheme);
    }

    /// <summary>Adds a provider identity to an existing user. Accounts are never merged: an identity of another user is refused.</summary>
    public static async Task<LinkResult> LinkAsync(
        HttpContext context, Guid userId, string provider, string subject, string? email, string? verifiedEmail)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!options.Allows(email)) return LinkResult.NotAllowed;

        var db = context.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var hasher = context.RequestServices.GetRequiredService<KeyedHasher>();
        if (!await db.Users.AnyAsync(u => u.Id == userId, context.RequestAborted)) return LinkResult.UnknownUser;

        var subjectHash = hasher.Subject(provider, subject);
        var existing = await db.UserIdentities
            .FirstOrDefaultAsync(i => i.Provider == provider && i.SubjectHash == subjectHash, context.RequestAborted);
        if (existing is not null && existing.UserId != userId) return LinkResult.InUseByAnotherUser;

        var result = existing is null ? LinkResult.Linked : LinkResult.AlreadyLinked;
        existing ??= db.UserIdentities.Add(new UserIdentity { UserId = userId, Provider = provider, SubjectHash = subjectHash }).Entity;
        existing.EmailHash = verifiedEmail is null ? null : hasher.Email(verifiedEmail);
        await db.SaveChangesAsync(context.RequestAborted);
        return result;
    }

    /// <summary>
    /// Handles the end of a provider round trip. Providers are mapped (MapInboundClaims off, GitHub by hand)
    /// to the raw OpenID Connect claim names sub, name, email and email_verified.
    /// </summary>
    public static async Task OnTicketReceived(TicketReceivedContext context, string provider)
    {
        var external = context.Principal!;
        var subject = external.FindFirstValue("sub");
        // Fail() is ignored at this stage (only HandleResponse stops the sign-in), so reject by redirecting.
        if (subject is null)
        {
            Reject(context, FailedError);
            return;
        }

        var email = external.FindFirstValue("email");
        var emailVerified = external.FindFirstValue("email_verified");
        // An email the provider says is unverified could belong to anyone, so it must not pass the allowlist.
        if (string.Equals(emailVerified, "false", StringComparison.OrdinalIgnoreCase)) email = null;
        // Search is stricter: only an email the provider positively vouches for may make someone findable.
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        var trusted = string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase)
                      || (options.Providers.TryGetValue(provider, out var settings) && settings.EmailsAreVerified);
        var verifiedEmail = trusted ? email : null;

        if (context.Properties?.Items.TryGetValue(LinkUserItem, out var linkUser) == true && linkUser is not null)
        {
            await LinkAsync(context, provider, subject, email, verifiedEmail, linkUser);
            return;
        }

        var name = external.FindFirstValue("name") ?? external.FindFirstValue("preferred_username");
        var principal = await SignInAsync(context.HttpContext, provider, subject, name, email, verifiedEmail);
        if (principal is null)
        {
            Reject(context, NotAllowedError);
            return;
        }

        context.Principal = principal;
    }

    /// <summary>
    /// The link branch of a callback. The remote handler runs before normal authentication, so the session is read
    /// explicitly; it must belong to the user who started the link, which stops a callback replayed in someone else's browser.
    /// </summary>
    private static async Task LinkAsync(
        TicketReceivedContext context, string provider, string subject, string? email, string? verifiedEmail, string linkUser)
    {
        var session = await context.HttpContext.AuthenticateAsync(AuthSetup.CookieScheme);
        if (!session.Succeeded || session.Principal!.IsToken() || session.Principal.UserId().ToString() != linkUser)
        {
            Reject(context, LinkFailedError);
            return;
        }

        var result = await LinkAsync(context.HttpContext, session.Principal.UserId(), provider, subject, email, verifiedEmail);
        switch (result)
        {
            case LinkResult.InUseByAnotherUser:
                Reject(context, IdentityInUseError);
                return;
            case LinkResult.NotAllowed:
                Reject(context, NotAllowedError);
                return;
            case LinkResult.UnknownUser:
                Reject(context, LinkFailedError);
                return;
        }

        // Keep the session the person already had; the new sign-in only added a way in.
        context.Principal = session.Principal;
    }

    public static Task OnRemoteFailure(RemoteFailureContext context)
    {
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserSignIn))
            .LogWarning(context.Failure, "Sign-in through {Scheme} failed", context.Scheme.Name);
        context.Response.Redirect($"/?authError={FailedError}");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static void Reject(TicketReceivedContext context, string error)
    {
        context.Response.Redirect($"/?authError={error}");
        context.HandleResponse();
    }
}
