using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>Which route value identifies the board an endpoint works on.</summary>
public enum BoardScope
{
    /// <summary>{boardId}</summary>
    Board,

    /// <summary>{noteId}: the note's board.</summary>
    Note,

    /// <summary>{bucketId}: the bucket's board.</summary>
    Bucket,

    /// <summary>{imageId}: the image's board.</summary>
    Image,
}

/// <summary>Endpoint metadata: the minimum role on the board in scope.</summary>
public sealed record BoardRoleRequirement(BoardRole Minimum, BoardScope Scope);

/// <summary>Endpoint metadata: deliberately not tied to one board (sign-in, the caller's own profile, listing boards, invites).</summary>
public sealed class NoBoardScopeMarker
{
    public static readonly NoBoardScopeMarker Instance = new();
}

/// <summary>
/// Boards are members-only. Every /api endpoint declares either <see cref="RequireBoardRole"/> or
/// <see cref="NoBoardScope{TBuilder}"/> (a test enforces this). Non-members get 404, so a board's existence does not leak;
/// members whose role is too low get 403. Token scopes are checked before this, by the fallback policy.
/// </summary>
public static class BoardAccess
{
    private const string RoleItem = "prikboard_board_role";

    public static RouteHandlerBuilder RequireBoardRole(this RouteHandlerBuilder builder, BoardRole minimum, BoardScope scope = BoardScope.Board) =>
        builder
            .WithMetadata(new BoardRoleRequirement(minimum, scope))
            .AddEndpointFilter((context, next) => Check(context, next, minimum, scope))
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    public static TBuilder NoBoardScope<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(NoBoardScopeMarker.Instance);

    /// <summary>The caller's role on the board in scope, as established by <see cref="RequireBoardRole"/>.</summary>
    public static BoardRole BoardRole(this HttpContext context) => (BoardRole)context.Items[RoleItem]!;

    /// <summary>The caller's role on a board, or null when they are not a member (or the board does not exist).</summary>
    public static Task<BoardRole?> RoleOnBoardAsync(PrikBoardDbContext db, Guid boardId, Guid principalId, CancellationToken ct) =>
        db.BoardMembers.Where(m => m.BoardId == boardId && m.PrincipalId == principalId)
            .Select(m => (BoardRole?)m.Role).FirstOrDefaultAsync(ct);

    private static async ValueTask<object?> Check(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, BoardRole minimum, BoardScope scope)
    {
        var http = context.HttpContext;
        var routeValue = scope switch
        {
            BoardScope.Board => "boardId",
            BoardScope.Note => "noteId",
            BoardScope.Bucket => "bucketId",
            _ => "imageId",
        };
        if (!Guid.TryParse(http.Request.RouteValues[routeValue]?.ToString(), out var id)) return TypedResults.NotFound();

        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var boardIds = scope switch
        {
            BoardScope.Board => db.Boards.Where(b => b.Id == id).Select(b => b.Id),
            BoardScope.Note => db.Notes.Where(n => n.Id == id).Select(n => n.BoardId),
            BoardScope.Bucket => db.Buckets.Where(b => b.Id == id).Select(b => b.BoardId),
            _ => db.Images.Where(i => i.Id == id).Select(i => i.BoardId),
        };
        var principalId = http.User.PrincipalId();
        var role = await db.BoardMembers
            .Where(m => m.PrincipalId == principalId && boardIds.Contains(m.BoardId))
            .Select(m => (BoardRole?)m.Role)
            .FirstOrDefaultAsync(http.RequestAborted);

        if (role is null) return TypedResults.NotFound();
        if (role < minimum)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Insufficient board role",
                detail: $"This needs the {minimum} role on the board; you are {role}.");
        }

        http.Items[RoleItem] = role.Value;
        return await next(context);
    }
}
