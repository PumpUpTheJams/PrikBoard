using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Turns an identity from an external provider into a PrikBoard user and session, or links it to the signed-in user.
/// Only keyed hashes of the provider's subject id and verified email are stored; names and emails pass through.
/// </summary>
public static partial class UserSignIn
{
    public const string FailedError = "failed";
    public const string InviteRequiredError = "invite_required";
    public const string LinkFailedError = "link_failed";
    public const string IdentityInUseError = "identity_in_use";

    /// <summary>
    /// Set on the challenge of a link flow (see <c>AuthEndpoints.Link</c>): the id of the user the new sign-in should join.
    /// The properties travel encrypted in the OAuth state, so they cannot be forged.
    /// </summary>
    public const string LinkUserItem = "prikboard_link_user";

    /// <summary>Key of the PostgreSQL advisory lock that lets one sign-up through at a time.</summary>
    private const long SignUpLock = 0x7072_696b_7369_676e; // "priksign"

    /// <summary>Who may get a new account at sign-in. People who already have one always get in.</summary>
    public readonly record struct SignUp(bool Anyone, string? InviteToken)
    {
        /// <summary>Real sign-ins: a newcomer needs an open invite link (or to be the very first user).</summary>
        public static SignUp Invite(string? token) => new(false, token);

        /// <summary>Test sign-ins, which stand in for people who were let in some other way.</summary>
        public static readonly SignUp Always = new(true, null);
    }

    public enum LinkResult
    {
        Linked,
        AlreadyLinked,
        InUseByAnotherUser,
        UnknownUser,
    }

    /// <summary>
    /// Finds the user for a provider identity, or creates one when <paramref name="signUp"/> allows it, and returns the
    /// principal for the session cookie; null when a newcomer may not join. The very first user may always join and
    /// becomes the admin. After that a newcomer needs an open invite link (<see cref="InviteIsOpenAsync"/>), which they
    /// then accept as usual. <paramref name="verifiedEmail"/> (only when the provider vouches for it) is hashed for
    /// exact-email search. Never links identities by email: that would let a provider with weak email checks take over accounts.
    /// <paramref name="picture"/> is the provider's picture URL, only offered to the person as an avatar.
    /// </summary>
    public static async Task<ClaimsPrincipal?> SignInAsync(
        HttpContext context, string provider, string subject, string? name, string? verifiedEmail, SignUp signUp, string? picture = null)
    {
        var db = context.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var hasher = context.RequestServices.GetRequiredService<KeyedHasher>();
        var ct = context.RequestAborted;
        var subjectHash = hasher.Subject(provider, subject);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var identity = await FindIdentityAsync(db, provider, subjectHash, ct);
        if (identity is null)
        {
            // One sign-up at a time, so two first sign-ins cannot both become the admin (nor one person two users).
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SignUpLock})", ct);
            identity = await FindIdentityAsync(db, provider, subjectHash, ct);
        }
        if (identity is null)
        {
            var first = !await db.Users.AnyAsync(ct);
            if (!first && !signUp.Anyone && !await InviteIsOpenAsync(db, signUp.InviteToken, ct)) return null;

            identity = new UserIdentity { User = new User { IsAdmin = first }, Provider = provider, SubjectHash = subjectHash };
            db.UserIdentities.Add(identity);
        }

        // Emails change at the provider; keep the hash current so search finds the address the person uses now.
        identity.EmailHash = verifiedEmail is null ? null : hasher.Email(verifiedEmail);
        identity.User!.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return UserPrincipal.Create(identity.User, provider, name, AuthSetup.CookieScheme, picture);
    }

    /// <summary>Whether <paramref name="token"/> is an invite link that is neither used nor expired, so its holder may join.</summary>
    public static async Task<bool> InviteIsOpenAsync(PrikBoardDbContext db, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = KeyedHasher.InviteToken(token);
        var now = DateTimeOffset.UtcNow;
        return await db.BoardInvites.AnyAsync(i => i.TokenHash == hash && i.AcceptedAt == null && i.ExpiresAt > now, ct);
    }

    /// <summary>
    /// The invite token in a sign-in's return URL. Someone who opens an invite link while signed out signs in with
    /// <c>/#/invite/{token}</c> as the return URL, which travels in the encrypted OAuth state.
    /// </summary>
    public static string? InviteTokenIn(string? returnUrl) =>
        returnUrl is not null && InviteInReturnUrl().Match(returnUrl) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// The user a delegated token's subject belongs to: the one a browser sign-in with the same provider finds, or null
    /// when there is none (or it was deleted). Tokens never create users; a person joins through an invite in the
    /// browser first. Leaves the stored email hash alone: a token carries no email the provider vouched for.
    /// </summary>
    public static async Task<Guid?> ResolveUserIdAsync(IServiceProvider services, string provider, string subject, CancellationToken ct)
    {
        var cache = services.GetRequiredService<IMemoryCache>();
        var key = ("user", provider, subject);
        if (cache.TryGetValue(key, out Guid cached))
        {
            if (await UserState.ExistsAsync(services, cached, ct)) return cached;
            cache.Remove(key);
            return null;
        }

        var db = services.GetRequiredService<PrikBoardDbContext>();
        var subjectHash = services.GetRequiredService<KeyedHasher>().Subject(provider, subject);
        var identity = await FindIdentityAsync(db, provider, subjectHash, ct);
        if (identity is null) return null;

        identity.User!.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        cache.Set(key, identity.User.Id, TimeSpan.FromMinutes(10));
        return identity.User.Id;
    }

    /// <summary>Adds a provider identity to an existing user. Accounts are never merged: an identity of another user is refused.</summary>
    public static async Task<LinkResult> LinkAsync(
        HttpContext context, Guid userId, string provider, string subject, string? verifiedEmail)
    {
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
    /// to the raw OpenID Connect claim names sub, name, email, email_verified and picture.
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
        // Only an email the provider vouches for may make someone findable; an explicit false always wins.
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        var trusted = string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase)
                      || (!string.Equals(emailVerified, "false", StringComparison.OrdinalIgnoreCase)
                          && options.Providers.TryGetValue(provider, out var settings) && settings.EmailsAreVerified);
        var verifiedEmail = trusted ? email : null;

        if (context.Properties?.Items.TryGetValue(LinkUserItem, out var linkUser) == true && linkUser is not null)
        {
            await LinkAsync(context, provider, subject, verifiedEmail, linkUser);
            return;
        }

        var name = external.FindFirstValue("name") ?? external.FindFirstValue("preferred_username");
        var principal = await SignInAsync(context.HttpContext, provider, subject, name, verifiedEmail,
            SignUp.Invite(InviteTokenIn(context.ReturnUri)), external.FindFirstValue("picture"));
        if (principal is null)
        {
            // A newcomer without an open invite link: nothing was stored about them.
            Reject(context, InviteRequiredError);
            return;
        }
        context.Principal = principal;
    }

    /// <summary>
    /// The link branch of a callback. The remote handler runs before normal authentication, so the session is read
    /// explicitly; it must belong to the user who started the link, which stops a callback replayed in someone else's browser.
    /// </summary>
    private static async Task LinkAsync(
        TicketReceivedContext context, string provider, string subject, string? verifiedEmail, string linkUser)
    {
        var session = await context.HttpContext.AuthenticateAsync(AuthSetup.CookieScheme);
        if (!session.Succeeded || session.Principal!.IsToken() || session.Principal.UserId().ToString() != linkUser)
        {
            Reject(context, LinkFailedError);
            return;
        }

        var result = await LinkAsync(context.HttpContext, session.Principal.UserId(), provider, subject, verifiedEmail);
        switch (result)
        {
            case LinkResult.InUseByAnotherUser:
                Reject(context, IdentityInUseError);
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

    private static Task<UserIdentity?> FindIdentityAsync(PrikBoardDbContext db, string provider, byte[] subjectHash, CancellationToken ct) =>
        db.UserIdentities.Include(i => i.User)
            .FirstOrDefaultAsync(i => i.Provider == provider && i.SubjectHash == subjectHash, ct);

    private static void Reject(TicketReceivedContext context, string error)
    {
        context.Response.Redirect($"/?authError={error}");
        context.HandleResponse();
    }

    [GeneratedRegex(@"#/invite/([\w-]+)")]
    private static partial Regex InviteInReturnUrl();
}
