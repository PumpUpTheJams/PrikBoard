using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Auth;

/// <summary>Which route value identifies the board an endpoint works on.</summary>
public enum BoardScope
{
    /// <summary>{boardId}</summary>
    Board,

    /// <summary>{cardId}: the card's board.</summary>
    Card,

    /// <summary>{bucketId}: the bucket's board.</summary>
    Bucket,

    /// <summary>{imageId}: the image's board.</summary>
    Image,

    /// <summary>{drawingId}: the drawing's board.</summary>
    Drawing,

    /// <summary>{shortId}: the board with that short id (<see cref="Domain.Board.ShortId"/>).</summary>
    ShortBoard,
}

/// <summary>Endpoint metadata: the minimum role on the board in scope.</summary>
public sealed record BoardRoleRequirement(BoardRole Minimum, BoardScope Scope);

/// <summary>Endpoint metadata: deliberately not tied to one board (sign-in, the caller's own profile, listing boards, invites).</summary>
public sealed class NoBoardScopeMarker
{
    public static readonly NoBoardScopeMarker Instance = new();
}

/// <summary>Endpoint metadata: a read that visitors of a public board may make too (see <see cref="BoardAccess.AllowVisitors"/>).</summary>
public sealed class VisitorsMarker
{
    public static readonly VisitorsMarker Instance = new();
}

/// <summary>Endpoint metadata: a board-scoped POST that changes nothing on the board (such as a lookup), so live clients are not told to reload.</summary>
public sealed class QuietMarker
{
    public static readonly QuietMarker Instance = new();
}

/// <summary>
/// Boards are members-only. Every /api endpoint declares either <see cref="RequireBoardRole"/> or
/// <see cref="NoBoardScope{TBuilder}"/> (a test enforces this). Non-members get 404, so a board's existence does not leak;
/// members whose role is too low get 403. Token scopes are checked before this, by the fallback policy. The one exception
/// is a public board: the reads marked <see cref="AllowVisitors"/> are open to anyone without membership, signed in or not
/// (but not to tokens), as a Viewer that is a visitor (<see cref="IsBoardVisitor"/>).
/// After a successful write (anything but GET and HEAD, unless marked <see cref="Quiet"/>) the filter tells the board's
/// live viewers to reload, so endpoints don't have to.
/// </summary>
public static class BoardAccess
{
    private const string RoleItem = "prikboard_board_role";
    private const string VisitorItem = "prikboard_board_visitor";

    public static RouteHandlerBuilder RequireBoardRole(this RouteHandlerBuilder builder, BoardRole minimum, BoardScope scope = BoardScope.Board) =>
        builder
            .WithMetadata(new BoardRoleRequirement(minimum, scope))
            .AddEndpointFilter((context, next) => Check(context, next, minimum, scope))
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    public static TBuilder NoBoardScope<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(NoBoardScopeMarker.Instance);

    /// <summary>Marks a board-scoped write that changes nothing visible on the board, so no live update is sent.</summary>
    public static RouteHandlerBuilder Quiet(this RouteHandlerBuilder builder) => builder.WithMetadata(QuietMarker.Instance);

    /// <summary>
    /// Opens a Viewer read to visitors of public boards: anyone with the link, signed in or not. Signed-in callers still
    /// need read scope (<see cref="AuthSetup.PublicReadPolicy"/>), and tokens still need membership.
    /// </summary>
    public static RouteHandlerBuilder AllowVisitors(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(VisitorsMarker.Instance).RequireAuthorization(AuthSetup.PublicReadPolicy);

    /// <summary>The caller's role on the board in scope, as established by <see cref="RequireBoardRole"/>.</summary>
    public static BoardRole BoardRole(this HttpContext context) => (BoardRole)context.Items[RoleItem]!;

    /// <summary>Whether the caller reads the board in scope as a visitor of a public board, not as a member.</summary>
    public static bool IsBoardVisitor(this HttpContext context) => context.Items.ContainsKey(VisitorItem);

    /// <summary>The caller's role on a board, or null when they are not a member (or the board does not exist).</summary>
    public static Task<BoardRole?> RoleOnBoardAsync(PrikBoardDbContext db, Guid boardId, Guid principalId, CancellationToken ct) =>
        db.BoardMembers.Where(m => m.BoardId == boardId && m.PrincipalId == principalId)
            .Select(m => (BoardRole?)m.Role).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The caller's role on a board, including personal access tokens, which act with their owner's role (capped and
    /// limited to their boards; see <see cref="BoardCaller"/>). Null when they have no access.
    /// </summary>
    public static async Task<BoardRole?> RoleOnBoardAsync(PrikBoardDbContext db, Guid boardId, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var caller = BoardCaller.Of(principal);
        return caller.Apply(boardId, await RoleOnBoardAsync(db, boardId, caller.MemberId, ct));
    }

    /// <summary>
    /// Who can be assigned to cards on a board: its members (people and issuer agents). The agents of members' personal
    /// access tokens are not: they work for the token's owner, who is assigned instead (CardEndpoints.WorksForAsync), and
    /// their avatar shows the agent at work. Connected Discord servers are members but only preview links, so they are
    /// left out.
    /// </summary>
    public static IQueryable<Guid> AssignableIds(PrikBoardDbContext db, Guid boardId)
    {
        var members = db.BoardMembers.Where(m => m.BoardId == boardId).Select(m => m.PrincipalId);
        var discord = db.Agents.Where(a => a.Issuer == Agent.DiscordIssuer).Select(a => a.Id);
        return members.Where(id => !discord.Contains(id));
    }

    /// <summary>The principals behind <see cref="AssignableIds"/>.</summary>
    public static Task<List<Principal>> PeopleAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct)
    {
        var ids = AssignableIds(db, boardId);
        return db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
    }

    /// <summary>The outcome of <see cref="AuthorizeAsync(HttpContext, BoardScope, Guid, BoardRole, bool)"/>: the board and role, or the response to send instead.</summary>
    public sealed record Authorization(Guid BoardId, BoardRole Role, IResult? Failure);

    /// <summary>
    /// Finds the board a board, card, bucket, image or drawing id belongs to and checks the caller's role on it in one query
    /// (a second one for visitors of public boards):
    /// 404 when the caller has no access (so existence does not leak), 403 when the role is too low. On success the role
    /// is available through <see cref="BoardRole(HttpContext)"/>. Used by the endpoint filter and by MCP tools.
    /// </summary>
    public static Task<Authorization> AuthorizeAsync(HttpContext http, BoardScope scope, Guid id, BoardRole minimum, bool visitors = false)
    {
        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        // Deleted items still belong to their board, so restoring them is authorized like any other write; the handlers
        // themselves load through the soft-delete filter and answer 404 for everything else.
        var boardIds = scope switch
        {
            BoardScope.Board => db.Boards.Where(b => b.Id == id).Select(b => b.Id),
            BoardScope.Card => db.Cards.WithDeleted().Where(n => n.Id == id).Select(n => n.BoardId),
            BoardScope.Bucket => db.Buckets.WithDeleted().Where(b => b.Id == id).Select(b => b.BoardId),
            BoardScope.Image => db.Images.WithDeleted().Where(i => i.Id == id).Select(i => i.BoardId),
            BoardScope.Drawing => db.Drawings.WithDeleted().Where(d => d.Id == id).Select(d => d.BoardId),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "This scope does not take an id."),
        };
        return AuthorizeAsync(http, boardIds, minimum, visitors);
    }

    /// <summary>Like the overload with an id, for the board with this short id.</summary>
    public static Task<Authorization> AuthorizeAsync(HttpContext http, string shortId, BoardRole minimum, bool visitors = false)
    {
        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        return AuthorizeAsync(http, db.Boards.Where(b => b.ShortId == shortId).Select(b => b.Id), minimum, visitors);
    }

    private static async Task<Authorization> AuthorizeAsync(HttpContext http, IQueryable<Guid> boardIds, BoardRole minimum, bool visitors)
    {
        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var caller = http.User.Identity?.IsAuthenticated == true ? BoardCaller.Of(http.User) : null;
        var membership = caller is null
            ? null
            : await db.BoardMembers
                .Where(m => m.PrincipalId == caller.MemberId && boardIds.Contains(m.BoardId))
                .Select(m => new { m.BoardId, m.Role })
                .FirstOrDefaultAsync(http.RequestAborted);

        if (caller is null || membership is null || caller.Apply(membership.BoardId, membership.Role) is not { } role)
        {
            // Not a member: a public board can still be read by anyone with the link, signed in or not. Tokens need membership.
            if (visitors && minimum == Domain.BoardRole.Viewer && !http.User.IsToken()
                && await db.Boards.Where(b => boardIds.Contains(b.Id) && b.IsPublic).Select(b => (Guid?)b.Id)
                    .FirstOrDefaultAsync(http.RequestAborted) is { } publicBoard)
            {
                http.Items[RoleItem] = Domain.BoardRole.Viewer;
                http.Items[VisitorItem] = true;
                return new Authorization(publicBoard, Domain.BoardRole.Viewer, null);
            }
            return new Authorization(Guid.Empty, default, TypedResults.NotFound());
        }
        if (role < minimum)
        {
            var detail = caller.Delegated && membership.Role >= minimum
                ? $"This needs the {minimum} role on the board; this access token is limited to {role}."
                : $"This needs the {minimum} role on the board; you are {role}.";
            return new Authorization(membership.BoardId, role, TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden, title: "Insufficient board role", detail: detail));
        }

        http.Items[RoleItem] = role;
        return new Authorization(membership.BoardId, role, null);
    }

    private static async ValueTask<object?> Check(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, BoardRole minimum, BoardScope scope)
    {
        var http = context.HttpContext;
        var visitors = http.GetEndpoint()?.Metadata.GetMetadata<VisitorsMarker>() is not null;
        Authorization access;
        if (scope == BoardScope.ShortBoard)
        {
            if (http.Request.RouteValues["shortId"]?.ToString() is not { } shortId) return TypedResults.NotFound();
            access = await AuthorizeAsync(http, shortId, minimum, visitors);
        }
        else
        {
            var routeValue = scope switch
            {
                BoardScope.Board => "boardId",
                BoardScope.Card => "cardId",
                BoardScope.Bucket => "bucketId",
                BoardScope.Image => "imageId",
                _ => "drawingId",
            };
            if (!Guid.TryParse(http.Request.RouteValues[routeValue]?.ToString(), out var id)) return TypedResults.NotFound();
            access = await AuthorizeAsync(http, scope, id, minimum, visitors);
        }
        if (access.Failure is not null) return access.Failure;

        var result = await next(context);

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && Succeeded(result)
            && http.GetEndpoint()?.Metadata.GetMetadata<QuietMarker>() is null)
        {
            await http.RequestServices.GetRequiredService<BoardEvents>().ChangedAsync(access.BoardId, http);
        }
        return result;
    }

    private static bool Succeeded(object? result) =>
        (result is INestedHttpResult nested ? nested.Result : result) is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 };
}
