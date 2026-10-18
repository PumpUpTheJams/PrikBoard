using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>Turns an identity from an external provider into a PrikBoard user and session.</summary>
public static class UserSignIn
{
    public const string NotAllowedError = "not_allowed";
    public const string FailedError = "failed";

    /// <summary>
    /// Finds or creates the user for a provider identity and returns the principal for the session cookie,
    /// or null when the allowlist rejects the email.
    /// </summary>
    public static async Task<ClaimsPrincipal?> SignInAsync(
        HttpContext context, string provider, string subject, string? name, string? email)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!options.Allows(email)) return null;

        var db = context.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Provider == provider && u.Subject == subject, context.RequestAborted);
        if (user is null)
        {
            user = new User { Provider = provider, Subject = subject, Name = "" };
            db.Users.Add(user);
        }

        // Names and emails change at the provider; keep the latest.
        user.Name = Truncate(name ?? email ?? subject, 200);
        user.Email = email is null ? null : Truncate(email, 320);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.RequestAborted);

        return UserPrincipal.Create(user, AuthSetup.CookieScheme);
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
        // An email the provider says is unverified could belong to anyone, so it must not pass the allowlist.
        if (string.Equals(external.FindFirstValue("email_verified"), "false", StringComparison.OrdinalIgnoreCase)) email = null;
        var name = external.FindFirstValue("name") ?? external.FindFirstValue("preferred_username");

        var principal = await SignInAsync(context.HttpContext, provider, subject, name, email);
        if (principal is null)
        {
            Reject(context, NotAllowedError);
            return;
        }

        context.Principal = principal;
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

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
