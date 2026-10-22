using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Personal access tokens, managed by the signed-in person. Sessions only: a token can never create, list or revoke
/// tokens, so a leaked one cannot dig itself in.
/// </summary>
public static class AccessTokenEndpoints
{
    public static RouteGroupBuilder MapAccessTokenEndpoints(this RouteGroupBuilder api)
    {
        var tokens = api.MapGroup("/me/tokens").WithTags("Access tokens").NoBoardScope();

        tokens.MapGet("/", ListAccessTokens)
            .WithName("listAccessTokens")
            .WithSummary("List your access tokens")
            .WithDescription("Your personal access tokens (never the tokens themselves), newest first. Browser sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        tokens.MapPost("/", CreateAccessToken)
            .WithName("createAccessToken")
            .WithSummary("Create an access token")
            .WithDescription("Creates a personal access token for an agent or script. It acts as its own agent on your boards, " +
                             "with your role there capped at Access (Viewer or Editor), optionally only on chosen boards. " +
                             "The token is returned once. Browser sessions only.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tokens.MapDelete("/{tokenId:guid}", RevokeAccessToken)
            .WithName("revokeAccessToken")
            .WithSummary("Revoke an access token")
            .WithDescription("Deletes the token; it stops working at once. What its agent did stays attributed to it. Browser sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<Results<Ok<List<AccessTokenDetail>>, ForbidHttpResult>> ListAccessTokens(
        ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var userId = principal.UserId();
        var tokens = await db.AccessTokens.AsNoTracking()
            .Where(t => t.OwnerId == userId)
            .Include(t => t.Agent)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(tokens.Select(t => AccessTokenDetail.From(t, t.Agent?.ShownName ?? t.Name ?? "App")).ToList());
    }

    private static async Task<Results<Created<CreatedAccessToken>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateAccessToken(
        CreateAccessTokenRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var userId = principal.UserId();

        if (request.Access is not (BoardRole.Viewer or BoardRole.Editor))
        {
            return Problems.Invalid("access", "Viewer or Editor. Tokens never get the Owner role.");
        }

        var boardIds = request.BoardIds?.Distinct().ToList();
        if (boardIds is not null)
        {
            var memberOf = await db.BoardMembers.CountAsync(m => m.PrincipalId == userId && boardIds.Contains(m.BoardId), ct);
            if (boardIds.Count == 0 || memberOf != boardIds.Count)
            {
                return Problems.Invalid("boardIds", "Choose at least one board, and only boards you are a member of; or leave it out for all of them.");
            }
        }

        if (await db.AccessTokens.CountAsync(t => t.OwnerId == userId, ct) >= AccessToken.MaxPerOwner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many access tokens",
                detail: $"You can have at most {AccessToken.MaxPerOwner}; revoke some you no longer use.");
        }

        var name = PatchText.OneLine(request.Name);
        var (token, hint) = AccessTokens.Generate();
        var record = new AccessToken
        {
            OwnerId = userId,
            TokenHash = AccessTokens.Hash(token),
            Hint = hint,
            MaxRole = request.Access,
            BoardIds = boardIds,
        };
        record.ExpiresAt = record.CreatedAt.AddDays(request.ExpiresInDays ?? AccessToken.DefaultDays);
        var agent = new Agent { Issuer = Agent.AccessTokenIssuer, ClientId = record.Id.ToString(), OwnerId = userId, DisplayName = name };
        record.AgentId = agent.Id;
        db.Agents.Add(agent);
        db.AccessTokens.Add(record);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/me/tokens/{record.Id}", new CreatedAccessToken(token, AccessTokenDetail.From(record, name)));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> RevokeAccessToken(
        Guid tokenId, ClaimsPrincipal principal, PrikBoardDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var userId = principal.UserId();
        var token = await db.AccessTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.OwnerId == userId, ct);
        if (token is null) return TypedResults.NotFound();

        // The agent stays, so what it did keeps its name.
        db.AccessTokens.Remove(token);
        await db.SaveChangesAsync(ct);
        AccessTokenHandler.Forget(cache, token.TokenHash);
        return TypedResults.NoContent();
    }
}
