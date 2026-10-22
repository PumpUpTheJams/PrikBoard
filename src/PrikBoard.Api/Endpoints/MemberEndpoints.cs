using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

public static class MemberEndpoints
{
    /// <summary>Rate-limiter policy for looking people up by email, so the endpoint cannot be used to test many addresses.</summary>
    public const string LookupLimit = "lookup";

    public static RouteGroupBuilder MapMemberEndpoints(this RouteGroupBuilder api)
    {
        var members = api.MapGroup("/boards/{boardId:guid}").WithTags("Members");

        members.MapGet("/members", ListBoardMembers)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("listBoardMembers")
            .WithSummary("List a board's members")
            .WithDescription("Returns the people and agents on the board with their roles, owners first.");

        members.MapPost("/members", AddBoardMember)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("addBoardMember")
            .WithSummary("Add a member")
            .WithDescription("Adds a person (found with findUserByEmail) or another principal to the board with a role. " +
                             "Fails with 409 when they are already a member; use updateBoardMember to change their role.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapPost("/agents", AddBoardAgent)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("addBoardAgent")
            .WithSummary("Add an agent")
            .WithDescription("Adds a machine client by its token issuer (see listTokenIssuers) and client id, even before it has " +
                             "called the API. Its access tokens then work on this board with the given role, within their scopes.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapPatch("/members/{principalId:guid}", UpdateBoardMember)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("updateBoardMember")
            .WithSummary("Change a member's role")
            .WithDescription("Sets a member's role. A board always keeps at least one owner (409 otherwise).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapDelete("/members/{principalId:guid}", RemoveBoardMember)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("removeBoardMember")
            .WithSummary("Remove a member, or leave the board")
            .WithDescription("Owners can remove anyone; everyone can remove themselves to leave. The last owner cannot leave (409).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapPost("/members/lookup", FindUserByEmail)
            .RequireBoardRole(BoardRole.Owner)
            .Quiet()
            .RequireRateLimiting(LookupLimit)
            .WithName("findUserByEmail")
            .WithSummary("Find a person by exact email")
            .WithDescription("Returns the people whose verified email is exactly this address (case-insensitive), to add them " +
                             "with addBoardMember. Nothing partial matches and no emails are stored or returned: PrikBoard " +
                             "keeps only keyed hashes. People only, not agents; rate limited.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<Ok<List<BoardMemberDetail>>> ListBoardMembers(
        Guid boardId, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var caller = principal.PrincipalId();
        var members = await db.BoardMembers.AsNoTracking().Include(m => m.Principal)
            .Where(m => m.BoardId == boardId).ToListAsync(ct);
        return TypedResults.Ok(members
            .Select(m => BoardMemberDetail.From(m, caller))
            .OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<Results<Created<BoardMemberDetail>, NotFound, ProblemHttpResult>> AddBoardMember(
        Guid boardId, AddBoardMemberRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, BoardEvents events,
        CancellationToken ct)
    {
        var target = await db.Principals.FindAsync([request.PrincipalId], ct);
        if (target is null) return TypedResults.NotFound();
        return await Add(db, events, boardId, target, request.Role, principal.PrincipalId(), ct);
    }

    private static async Task<Results<Created<BoardMemberDetail>, NotFound, ProblemHttpResult, ValidationProblem>> AddBoardAgent(
        Guid boardId, AddBoardAgentRequest request, ClaimsPrincipal principal, PrikBoardDbContext db,
        IOptions<AuthOptions> options, HttpContext http, BoardEvents events, CancellationToken ct)
    {
        if (!options.Value.Tokens.Issuers.TryGetValue(request.Issuer, out var issuer) || string.IsNullOrWhiteSpace(issuer.Authority))
        {
            return Problems.Invalid("issuer", "Not a configured token issuer; see listTokenIssuers.");
        }

        var agentId = await AgentRegistry.ResolveAsync(http.RequestServices, request.Issuer, request.ClientId.Trim(), ct);
        var agent = (await db.Agents.FindAsync([agentId], ct))!;
        if (agent.DisplayName is null && !string.IsNullOrWhiteSpace(request.DisplayName)) agent.DisplayName = request.DisplayName.Trim();

        var result = await Add(db, events, boardId, agent, request.Role, principal.PrincipalId(), ct);
        return result.Result switch
        {
            Created<BoardMemberDetail> created => created,
            NotFound notFound => notFound,
            ProblemHttpResult problem => problem,
            _ => throw new InvalidOperationException(),
        };
    }

    private static async Task<Results<Created<BoardMemberDetail>, NotFound, ProblemHttpResult>> Add(
        PrikBoardDbContext db, BoardEvents events, Guid boardId, Principal target, BoardRole role, Guid caller, CancellationToken ct)
    {
        if (await db.BoardMembers.AnyAsync(m => m.BoardId == boardId && m.PrincipalId == target.Id, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already a member",
                detail: "Use updateBoardMember to change their role.");
        }

        var member = new BoardMember { BoardId = boardId, Principal = target, PrincipalId = target.Id, Role = role };
        db.BoardMembers.Add(member);
        await db.SaveChangesAsync(ct);
        await events.AccessChangedAsync(boardId, target.Id, role);
        await events.BoardsChangedAsync([target.Id]);
        return TypedResults.Created($"/api/boards/{boardId}/members/{target.Id}", BoardMemberDetail.From(member, caller));
    }

    private static async Task<Results<Ok<BoardMemberDetail>, NotFound, ProblemHttpResult>> UpdateBoardMember(
        Guid boardId, Guid principalId, UpdateBoardMemberRequest request, ClaimsPrincipal principal,
        PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockBoard(db, boardId, ct);

        var member = await db.BoardMembers.Include(m => m.Principal)
            .FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == principalId, ct);
        if (member is null) return TypedResults.NotFound();
        if (member.Role == BoardRole.Owner && request.Role != BoardRole.Owner && await IsLastOwner(db, boardId, ct))
        {
            return LastOwner();
        }

        member.Role = request.Role;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await events.AccessChangedAsync(boardId, principalId, member.Role);
        await events.BoardsChangedAsync([principalId]);
        return TypedResults.Ok(BoardMemberDetail.From(member, principal.PrincipalId()));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveBoardMember(
        Guid boardId, Guid principalId, ClaimsPrincipal principal, HttpContext http, PrikBoardDbContext db, BoardEvents events,
        CancellationToken ct)
    {
        var self = principal.PrincipalId() == principalId;
        if (!self && http.BoardRole() != BoardRole.Owner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Insufficient board role",
                detail: "Only owners can remove other members; you can only remove yourself.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockBoard(db, boardId, ct);

        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == principalId, ct);
        if (member is null) return TypedResults.NotFound();
        if (member.Role == BoardRole.Owner && await IsLastOwner(db, boardId, ct)) return LastOwner();

        db.BoardMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await events.AccessChangedAsync(boardId, principalId, null);
        await events.BoardsChangedAsync([principalId]);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<UserMatch>>, ForbidHttpResult>> FindUserByEmail(
        Guid boardId, FindUserRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, KeyedHasher hasher, CancellationToken ct)
    {
        // Only people search, so a token (possibly an agent following instructions it read somewhere) cannot probe addresses.
        if (principal.IsToken()) return TypedResults.Forbid();

        var hash = hasher.Email(request.Email);
        var users = await db.UserIdentities.AsNoTracking()
            .Where(i => i.EmailHash == hash)
            .Select(i => i.User!)
            .Distinct()
            .ToListAsync(ct);
        var roles = await db.BoardMembers.AsNoTracking()
            .Where(m => m.BoardId == boardId)
            .ToDictionaryAsync(m => m.PrincipalId, m => m.Role, ct);

        return TypedResults.Ok(users
            .Select(u => new UserMatch(u.Id, u.ShownName, roles.TryGetValue(u.Id, out var role) ? role : null))
            .ToList());
    }

    /// <summary>Serializes membership changes per board, so two owners demoting each other cannot leave it ownerless.</summary>
    internal static Task LockBoard(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Boards" WHERE "Id" = {boardId} FOR UPDATE""", ct);

    internal static async Task<bool> IsLastOwner(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        await db.BoardMembers.CountAsync(m => m.BoardId == boardId && m.Role == BoardRole.Owner, ct) <= 1;

    internal static ProblemHttpResult LastOwner() =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Last owner",
            detail: "A board needs at least one owner. Make someone else an owner first.");
}
