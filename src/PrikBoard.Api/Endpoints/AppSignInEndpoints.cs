using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Signing PrikBoard's own apps (the VS Code extension) in as a person, like an OAuth authorization code with PKCE.
/// The app opens PrikBoard's connect page; the signed-in person approves (approveAppSignIn) and the page hands the
/// one-time code to the app, through a loopback redirect or by pasting. The app trades it, with its code verifier, for
/// an <see cref="AccessTokenKind.App"/> token (exchangeAppSignIn). The code alone is useless, so one that leaks through
/// browser history or another local program cannot be spent.
/// </summary>
public static class AppSignInEndpoints
{
    /// <summary>Rate-limiter policy for trading codes, which anyone can call.</summary>
    public const string ExchangeLimit = "app-sign-in";

    public static RouteGroupBuilder MapAppSignInEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/me/app-sign-ins", ApproveAppSignIn)
            .WithTags("Access tokens")
            .NoBoardScope()
            .WithName("approveAppSignIn")
            .WithSummary("Approve signing an app in")
            .WithDescription("Called by PrikBoard's connect page when you allow an app (the VS Code extension) to use PrikBoard as you. " +
                             "Returns a one-time code that the app trades, with its PKCE verifier, for a token (exchangeAppSignIn). " +
                             "Browser sessions from PrikBoard's own pages only.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/auth/app-sign-ins/token", ExchangeAppSignIn)
            .WithTags("Access tokens")
            .NoBoardScope()
            .AllowAnonymous()
            .RequireRateLimiting(ExchangeLimit)
            .WithName("exchangeAppSignIn")
            .WithSummary("Trade a sign-in code for a token")
            .WithDescription("The app sends the one-time code from approveAppSignIn and its PKCE code verifier, and gets a personal " +
                             "access token that acts as the person who approved, for 90 days. Each code works once, for 5 minutes.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<Results<Ok<AppSignInCode>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> ApproveAppSignIn(
        ApproveAppSignInRequest request, ClaimsPrincipal principal, HttpRequest http, PrikBoardDbContext db, CancellationToken ct)
    {
        // A token cannot sign apps in, and another site cannot make the person's browser approve one.
        if (principal.IsToken() || !SameOrigin.Check(http)) return TypedResults.Forbid();
        var userId = principal.UserId();

        if (request.RedirectUri is not null && !IsLoopback(request.RedirectUri))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["redirectUri"] = ["Only a loopback address such as http://127.0.0.1:<port>/callback; leave it out to paste the code."],
            });
        }
        if (!Base64Url.IsValid(request.CodeChallenge))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["codeChallenge"] = ["The base64url SHA-256 of the code verifier (S256)."],
            });
        }

        var now = DateTimeOffset.UtcNow;
        await db.AppSignInGrants.Where(g => g.OwnerId == userId && g.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await db.AppSignInGrants.CountAsync(g => g.OwnerId == userId, ct) >= AppSignInGrant.MaxPendingPerOwner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many sign-ins waiting",
                detail: "Finish the sign-ins you started, or wait a few minutes for them to expire.");
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var grant = new AppSignInGrant
        {
            OwnerId = userId,
            CodeHash = AccessTokens.Hash(code),
            CodeChallenge = request.CodeChallenge,
            Name = CleanName(request.Name),
            ExpiresAt = now.Add(AppSignInGrant.Lifetime),
        };
        db.AppSignInGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new AppSignInCode(code, grant.ExpiresAt));
    }

    private static async Task<Results<Created<CreatedAccessToken>, ValidationProblem, ProblemHttpResult>> ExchangeAppSignIn(
        ExchangeAppSignInRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var hash = AccessTokens.Hash(request.Code);
        var grant = await db.AppSignInGrants.AsNoTracking().FirstOrDefaultAsync(g => g.CodeHash == hash, ct);

        // Spent on any attempt, right or wrong, and only once even when two requests race.
        var spent = grant is not null && await db.AppSignInGrants.Where(g => g.Id == grant.Id).ExecuteDeleteAsync(ct) == 1;
        if (grant is null || !spent || grant.ExpiresAt <= DateTimeOffset.UtcNow || !Matches(request.CodeVerifier, grant.CodeChallenge))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["Unknown, used or expired code, or the wrong code verifier. Sign in again."],
            });
        }

        if (await db.AccessTokens.CountAsync(t => t.OwnerId == grant.OwnerId, ct) >= AccessToken.MaxPerOwner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many access tokens",
                detail: $"You can have at most {AccessToken.MaxPerOwner}; revoke some you no longer use in PrikBoard under Account.");
        }

        var (token, hint) = AccessTokens.Generate();
        var record = new AccessToken
        {
            Kind = AccessTokenKind.App,
            OwnerId = grant.OwnerId,
            Name = grant.Name,
            TokenHash = AccessTokens.Hash(token),
            Hint = hint,
            MaxRole = BoardRole.Editor,
        };
        record.ExpiresAt = record.CreatedAt.AddDays(AccessToken.DefaultDays);
        db.AccessTokens.Add(record);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/me/tokens/{record.Id}", new CreatedAccessToken(token, AccessTokenDetail.From(record, record.Name)));
    }

    /// <summary>http on 127.0.0.1, localhost or [::1] with an explicit port: the app on the person's own machine.</summary>
    internal static bool IsLoopback(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttp
        && parsed.IsLoopback
        && parsed.Host is "127.0.0.1" or "localhost" or "[::1]"
        && !parsed.IsDefaultPort
        && parsed.UserInfo.Length == 0
        && parsed.Fragment.Length == 0;

    /// <summary>Whether the PKCE verifier hashes (S256) to the challenge.</summary>
    internal static bool Matches(string verifier, string challenge)
    {
        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    internal static string CleanName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? "App" : clean;
    }
}
