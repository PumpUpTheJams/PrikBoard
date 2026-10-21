using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

public static class InviteEndpoints
{
    /// <summary>Rate-limiter policy for using invite tokens, so they cannot be guessed (they are 256 random bits anyway).</summary>
    public const string InviteLimit = "invites";

    public static RouteGroupBuilder MapInviteEndpoints(this RouteGroupBuilder api)
    {
        var board = api.MapGroup("/boards/{boardId:guid}/invites").WithTags("Invites");

        board.MapGet("/", ListBoardInvites)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listBoardInvites")
            .WithSummary("List pending invite links")
            .WithDescription("Returns the board's invite links that are neither used nor expired. The links themselves are not shown again.");

        board.MapPost("/", CreateBoardInvite)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("createBoardInvite")
            .WithSummary("Create an invite link")
            .WithDescription("Creates a single-use link that makes whoever accepts it (after signing in) a member with the role. " +
                             "It expires after expiresInDays (default 7). The token is only returned now; PrikBoard keeps a hash.")
            .ProducesValidationProblem();

        board.MapDelete("/{inviteId:guid}", RevokeBoardInvite)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("revokeBoardInvite")
            .WithSummary("Revoke an invite link");

        var invites = api.MapGroup("/invites").WithTags("Invites").NoBoardScope().RequireRateLimiting(InviteLimit);

        invites.MapPost("/preview", PreviewInvite)
            .WithName("previewInvite")
            .WithSummary("Look at an invite link")
            .WithDescription("Shows which board and role an invite token grants. Unknown, used, revoked and expired tokens all give 404. People only.")
            .Produces(StatusCodes.Status429TooManyRequests);

        invites.MapPost("/accept", AcceptInvite)
            .WithName("acceptInvite")
            .WithSummary("Accept an invite link")
            .WithDescription("Spends the token and makes you a member (keeping a higher role you already had). People only: " +
                             "agents are added by an owner with addBoardAgent instead.")
            .Produces(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<Ok<List<BoardInviteDetail>>> ListBoardInvites(Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var invites = await db.BoardInvites.AsNoTracking().Include(i => i.CreatedBy)
            .Where(i => i.BoardId == boardId && i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(invites.Select(i => new BoardInviteDetail(i.Id, i.Role, i.CreatedBy?.ShownName, i.CreatedAt, i.ExpiresAt)).ToList());
    }

    private static async Task<Created<CreatedInvite>> CreateBoardInvite(
        Guid boardId, CreateInviteRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var invite = new BoardInvite
        {
            BoardId = boardId,
            Role = request.Role,
            TokenHash = KeyedHasher.InviteToken(token),
            CreatedById = principal.PrincipalId(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays ?? BoardInvite.DefaultDays),
        };
        db.BoardInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        // A hash route: the token stays in the browser (fragments are not sent to servers or in Referer headers).
        return TypedResults.Created($"/api/boards/{boardId}/invites/{invite.Id}",
            new CreatedInvite(invite.Id, invite.Role, invite.ExpiresAt, token, $"/#/invite/{token}"));
    }

    private static async Task<Results<NoContent, NotFound>> RevokeBoardInvite(Guid boardId, Guid inviteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var deleted = await db.BoardInvites.Where(i => i.Id == inviteId && i.BoardId == boardId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<InvitePreview>, NotFound, ForbidHttpResult>> PreviewInvite(
        InviteTokenRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();

        var hash = KeyedHasher.InviteToken(request.Token);
        var now = DateTimeOffset.UtcNow;
        var invite = await db.BoardInvites.AsNoTracking().Include(i => i.CreatedBy)
            .FirstOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null && i.ExpiresAt > now, ct);
        if (invite is null) return TypedResults.NotFound();

        var board = await db.Boards.AsNoTracking().FirstAsync(b => b.Id == invite.BoardId, ct);
        var yourRole = await BoardAccess.RoleOnBoardAsync(db, invite.BoardId, principal.UserId(), ct);
        return TypedResults.Ok(new InvitePreview(board.Id, board.Name, invite.Role, invite.CreatedBy?.ShownName, invite.ExpiresAt, yourRole));
    }

    private static async Task<Results<Ok<AcceptedInvite>, NotFound, ForbidHttpResult>> AcceptInvite(
        InviteTokenRequest request, ClaimsPrincipal principal, HttpContext http, PrikBoardDbContext db, BoardEvents events,
        CancellationToken ct)
    {
        // An agent could have read the link on a card; joining boards that way would bypass the owner.
        if (principal.IsToken()) return TypedResults.Forbid();

        var userId = principal.UserId();
        var hash = KeyedHasher.InviteToken(request.Token);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Spend the token in one conditional update, so two people racing for the same link cannot both get in.
        var spent = await db.BoardInvites
            .Where(i => i.TokenHash == hash && i.AcceptedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now).SetProperty(i => i.AcceptedById, userId), ct);
        if (spent == 0) return TypedResults.NotFound();

        var invite = await db.BoardInvites.AsNoTracking().FirstAsync(i => i.TokenHash == hash, ct);
        await MemberEndpoints.LockBoard(db, invite.BoardId, ct);
        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == invite.BoardId && m.PrincipalId == userId, ct);
        if (member is null)
        {
            member = new BoardMember { BoardId = invite.BoardId, PrincipalId = userId, Role = invite.Role };
            db.BoardMembers.Add(member);
        }
        else if (invite.Role > member.Role)
        {
            member.Role = invite.Role;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // Not board-scoped, so the access filter does not announce it: the members list changed.
        await events.ChangedAsync(invite.BoardId, http);
        await events.AccessChangedAsync(invite.BoardId, userId, member.Role);
        await events.BoardsChangedAsync([userId]);
        return TypedResults.Ok(new AcceptedInvite(invite.BoardId, member.Role));
    }
}
