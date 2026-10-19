using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Running the instance: every board and user, anyone's board roles, admins and deleting users. Admins only, and sessions
/// only (like token management), so there are no MCP tools for it. Admins see who is on a board, never what is on it.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Key of the PostgreSQL advisory lock that serialises admin changes, so there is always an admin left.</summary>
    private const long AdminLock = 0x7072_696b_6164_6d6e; // "prikadmn"

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").NoBoardScope();

        admin.MapGet("/boards", ListBoards)
            .RequireAdmin()
            .WithName("adminListBoards")
            .WithSummary("List every board")
            .WithDescription("Admins only. Every board on this PrikBoard, newest first, with its members and their roles. " +
                             "Shows who is on a board, not what is on it.");

        admin.MapDelete("/boards/{boardId:guid}", DeleteBoard)
            .RequireAdmin()
            .WithName("adminDeleteBoard")
            .WithSummary("Delete any board")
            .WithDescription("Admins only. Permanently deletes the board with all of its buckets, notes, images, members " +
                             "and invites, like deleteBoard does for its owners. Admins don't need to be a member.");

        admin.MapGet("/users", ListUsers)
            .RequireAdmin()
            .WithName("adminListUsers")
            .WithSummary("List every user")
            .WithDescription("Admins only. Everyone with an account, oldest first, with whether they are an admin, how many " +
                             "boards they are on and which boards they are the only owner of.");

        admin.MapPut("/boards/{boardId:guid}/members/{userId:guid}", SetBoardMember)
            .RequireAdmin()
            .WithName("adminSetBoardMember")
            .WithSummary("Give a person a role on any board")
            .WithDescription("Admins only. Adds the user to the board with the role, or changes the role they have. " +
                             "A board always keeps at least one owner (409 otherwise).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapDelete("/boards/{boardId:guid}/members/{principalId:guid}", RemoveBoardMember)
            .RequireAdmin()
            .WithName("adminRemoveBoardMember")
            .WithSummary("Take anyone off any board")
            .WithDescription("Admins only. Removes a person or agent from the board. The last owner cannot be removed (409).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPatch("/users/{userId:guid}", UpdateUser)
            .RequireAdmin()
            .WithName("adminUpdateUser")
            .WithSummary("Make a user an admin, or take it away")
            .WithDescription("Admins only. There is always at least one admin (409 otherwise).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapDelete("/users/{userId:guid}", DeleteUser)
            .RequireAdmin()
            .WithName("adminDeleteUser")
            .WithSummary("Delete a user")
            .WithDescription("Admins only. Deletes the account with its sign-ins, memberships and access tokens; their sessions " +
                             "stop working at once. Notes they wrote stay. Refused for yourself (400) and while they are the " +
                             "only owner of a board (409, with those boards in the problem's boards field).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <summary>Lets only signed-in admins through: 403 for everyone else, and for every token.</summary>
    private static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                if (http.User.IsToken())
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sessions only",
                        detail: "Running PrikBoard is done in the browser; access tokens cannot use the admin endpoints.");
                }

                var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
                var userId = http.User.UserId();
                if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin, http.RequestAborted))
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Admins only",
                        detail: "Only admins of this PrikBoard can do this.");
                }
                return await next(context);
            })
            .ProducesProblem(StatusCodes.Status403Forbidden);

    private static async Task<Ok<List<AdminBoard>>> ListBoards(PrikBoardDbContext db, CancellationToken ct)
    {
        var boards = await db.Boards.AsNoTracking().OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        var members = (await db.BoardMembers.AsNoTracking().Include(m => m.Principal).ToListAsync(ct))
            .ToLookup(m => m.BoardId);

        return TypedResults.Ok(boards.Select(b => new AdminBoard(b.Id, b.Name, b.CreatedAt, b.UpdatedAt, members[b.Id]
                .Select(AdminBoardMember.From)
                .OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .ToList());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBoard(
        Guid boardId, HttpContext http, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        var members = await BoardEvents.MembersOf(db, boardId, ct);
        var deleted = await db.Boards.Where(b => b.Id == boardId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return TypedResults.NotFound();
        // Not board-scoped, so the access filter does not announce it: open viewers reload and find it gone.
        await events.ChangedAsync(boardId, http);
        await events.BoardsChangedAsync(members);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<AdminUser>>> ListUsers(ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(ct);
        var memberships = await db.BoardMembers.AsNoTracking().ToListAsync(ct);
        var boardNames = await db.Boards.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var soleOwners = memberships.Where(m => m.Role == BoardRole.Owner).GroupBy(m => m.BoardId)
            .Where(g => g.Count() == 1).Select(g => g.Single()).ToLookup(m => m.PrincipalId, m => m.BoardId);
        var boardCounts = memberships.GroupBy(m => m.PrincipalId).ToDictionary(g => g.Key, g => g.Count());
        var caller = principal.UserId();

        return TypedResults.Ok(users.Select(u => new AdminUser(u.Id, u.ShownName, u.IsAdmin, u.Id == caller, u.CreatedAt,
                u.LastSeenAt, boardCounts.GetValueOrDefault(u.Id),
                soleOwners[u.Id].Select(id => new AdminBoardRef(id, boardNames[id]))
                    .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList());
    }

    private static async Task<Results<Ok<AdminBoardMember>, NotFound, ProblemHttpResult>> SetBoardMember(
        Guid boardId, Guid userId, AdminSetBoardMemberRequest request, HttpContext http, PrikBoardDbContext db,
        BoardEvents events, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || !await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);

        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == userId, ct);
        if (member is null)
        {
            member = new BoardMember { BoardId = boardId, PrincipalId = userId, Role = request.Role };
            db.BoardMembers.Add(member);
        }
        else if (member.Role == BoardRole.Owner && request.Role != BoardRole.Owner && await MemberEndpoints.IsLastOwner(db, boardId, ct))
        {
            return MemberEndpoints.LastOwner();
        }
        else
        {
            member.Role = request.Role;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        member.Principal = user;
        // Not board-scoped, so the access filter does not announce it: the members list changed.
        await events.ChangedAsync(boardId, http);
        await events.AccessChangedAsync(boardId, userId, member.Role);
        await events.BoardsChangedAsync([userId]);
        return TypedResults.Ok(AdminBoardMember.From(member));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveBoardMember(
        Guid boardId, Guid principalId, HttpContext http, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);

        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == principalId, ct);
        if (member is null) return TypedResults.NotFound();
        if (member.Role == BoardRole.Owner && await MemberEndpoints.IsLastOwner(db, boardId, ct)) return MemberEndpoints.LastOwner();

        db.BoardMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await events.ChangedAsync(boardId, http);
        await events.AccessChangedAsync(boardId, principalId, null);
        await events.BoardsChangedAsync([principalId]);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateUser(
        Guid userId, AdminUpdateUserRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdminLock})", ct);

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return TypedResults.NotFound();
        if (request.IsAdmin is { } isAdmin && isAdmin != user.IsAdmin)
        {
            if (!isAdmin && await db.Users.CountAsync(u => u.IsAdmin, ct) <= 1)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Last admin",
                    detail: "PrikBoard needs at least one admin. Make someone else an admin first.");
            }
            user.IsAdmin = isAdmin;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteUser(
        Guid userId, ClaimsPrincipal principal, HttpContext http, PrikBoardDbContext db, BoardEvents events, IMemoryCache cache,
        CancellationToken ct)
    {
        if (userId == principal.UserId())
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That's you",
                detail: "Admins cannot delete themselves. Ask another admin.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdminLock})", ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return TypedResults.NotFound();

        // Lock the boards they own (in a fixed order), so nobody demotes the other owner while we check.
        var boardIds = await db.BoardMembers.Where(m => m.PrincipalId == userId).Select(m => m.BoardId).ToListAsync(ct);
        var owned = await db.BoardMembers.Where(m => m.PrincipalId == userId && m.Role == BoardRole.Owner)
            .Select(m => m.BoardId).OrderBy(id => id).ToListAsync(ct);
        foreach (var boardId in owned) await MemberEndpoints.LockBoard(db, boardId, ct);

        var soleOwned = await db.Boards
            .Where(b => owned.Contains(b.Id) && b.Members.Count(m => m.Role == BoardRole.Owner) == 1)
            .OrderBy(b => b.Name)
            .Select(b => new AdminBoardRef(b.Id, b.Name))
            .ToListAsync(ct);
        if (soleOwned.Count > 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Only owner",
                detail: $"They are the only owner of {soleOwned.Count} board(s). Make someone else an owner of each first.",
                extensions: new Dictionary<string, object?> { ["boards"] = soleOwned });
        }

        var tokenHashes = await db.AccessTokens.Where(t => t.OwnerId == userId).Select(t => t.TokenHash).ToListAsync(ct);
        // The database cascades to their sign-ins, memberships and tokens, and clears them from notes, images and invites.
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);

        UserState.Forget(cache, userId);
        foreach (var hash in tokenHashes) AccessTokenHandler.Forget(cache, hash);
        foreach (var boardId in boardIds)
        {
            await events.ChangedAsync(boardId, http);
            await events.AccessChangedAsync(boardId, userId, null);
        }
        await events.BoardsChangedAsync([userId]);
        return TypedResults.NoContent();
    }
}
