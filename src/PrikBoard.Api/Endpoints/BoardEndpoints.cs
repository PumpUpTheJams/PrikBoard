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

public static class BoardEndpoints
{
    public static RouteGroupBuilder MapBoardEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Boards");

        boards.MapGet("/", ListBoards)
            .NoBoardScope()
            .WithName("listBoards")
            .WithSummary("List your boards")
            .WithDescription("Returns the boards you are a member of, without their buckets and cards, newest first, with your role on each.");

        boards.MapPost("/", CreateBoard)
            .NoBoardScope()
            .WithName("createBoard")
            .WithSummary("Create a board")
            .WithDescription("Creates a board with the default buckets \"To do\", \"Doing\" and \"Done\" side by side. You become its owner " +
                             "(with a personal access token, the token's owner does). Tokens limited to chosen boards cannot create boards.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        boards.MapGet("/{boardId:guid}", GetBoard)
            .RequireBoardRole(BoardRole.Viewer)
            .AllowVisitors()
            .WithName("getBoard")
            .WithSummary("Get a full board snapshot")
            .WithDescription("Returns the board with its agent rules, buckets, cards, images, drawings, tags and people, and where " +
                             "everything sits on the canvas. This is the best single call to understand the current state of a project. " +
                             "A public board answers anyone, signed in or not (visiting is then true).");

        boards.MapGet($"/{{shortId:length({Board.ShortIdLength})}}", GetBoardByShortId)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.ShortBoard)
            .AllowVisitors()
            .WithName("getBoardByShortId")
            .WithSummary("Get a board by its short id")
            .WithDescription("Like getBoard, for the short id in the board's web address (/#/boards/{shortId}). " +
                             "A public board answers anyone, signed in or not (visiting is then true).");

        boards.MapGet("/{boardId:guid}/avatars/{principalId:guid}", GetBoardAvatar)
            .RequireBoardRole(BoardRole.Viewer)
            .AllowVisitors()
            .WithName("getBoardAvatar")
            .WithSummary("Download the avatar of someone on a board")
            .WithDescription("Returns the picture of a member, creator or assignee of the board, as getAvatar does. avatarUrl points " +
                             "here for visitors of a public board that shows people. 404 for anyone else, and for visitors when the " +
                             "board doesn't show people.");

        boards.MapPatch("/{boardId:guid}", UpdateBoard)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("updateBoard")
            .WithSummary("Update a board")
            .WithDescription("Changes the board name, key, description, agent rules and/or whether it is public (and shows people to " +
                             "its visitors). Omitted fields stay unchanged. Only owners can do this, so agents cannot change the rules " +
                             "they follow. The short id never changes, so links keep working.")
            .ProducesValidationProblem();

        boards.MapDelete("/{boardId:guid}", DeleteBoard)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("deleteBoard")
            .WithSummary("Delete a board")
            .WithDescription("Permanently deletes the board with everything on it, its members and its invites. There is no undo.");

        return api;
    }

    internal static async Task<Ok<List<BoardSummary>>> ListBoards(ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        // A personal access token lists its owner's boards, with its capped role and only the boards it is limited to.
        var caller = BoardCaller.Of(principal);
        // Projected, so the boards' rules (up to 20,000 characters each) stay in the database.
        var boards = await db.BoardMembers.AsNoTracking()
            .Where(m => m.PrincipalId == caller.MemberId)
            .OrderByDescending(m => m.Board!.CreatedAt)
            .Select(m => new BoardSummary(m.Board!.Id, m.Board.ShortId, m.Board.Key, m.Board.Name, m.Board.Description,
                m.Board.CreatedAt, m.Board.UpdatedAt, m.Role, m.Board.IsPublic, m.Board.PublicShowsPeople))
            .ToListAsync(ct);
        return TypedResults.Ok(boards
            .Select(b => caller.Apply(b.Id, b.YourRole) is { } role ? b with { YourRole = role } : null)
            .OfType<BoardSummary>()
            .ToList());
    }

    private static async Task<Results<Created<BoardDetail>, ProblemHttpResult>> CreateBoard(
        CreateBoardRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var caller = BoardCaller.Of(principal);
        if (caller.Boards is not null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Limited access token",
                detail: "This access token is limited to chosen boards, so it cannot create new ones.");
        }

        var board = new Board
        {
            ShortId = await NewShortIdAsync(db, ct),
            Name = request.Name,
            Key = Board.KeyFrom(request.Name),
            Description = PatchText.Apply(null, request.Description),
        };
        foreach (var name in Board.DefaultBuckets)
        {
            var (x, y) = Layout.NextBucketSpot(board.Buckets);
            board.Buckets.Add(new Bucket
            {
                Name = name,
                X = x,
                Y = y,
                Width = Layout.DefaultBucketWidth,
                Height = Layout.DefaultBucketHeight,
                // Distinct timestamps keep the list and stacking order of the defaults well defined.
                CreatedAt = board.CreatedAt.AddMilliseconds(board.Buckets.Count),
                StackedAt = board.CreatedAt.AddMilliseconds(board.Buckets.Count),
            });
        }

        // A personal access token creates the board for its owner, who owns it; the token keeps its capped role.
        board.Members.Add(new BoardMember { PrincipalId = caller.MemberId, Role = BoardRole.Owner });

        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);
        var people = await BoardAccess.PeopleAsync(db, board.Id, ct);
        return TypedResults.Created($"/api/boards/{board.Id}",
            BoardDetail.From(board, caller.Apply(board.Id, BoardRole.Owner)!.Value, new Dictionary<Guid, Principal>(), people, principal.PrincipalId()));
    }

    internal static Task<Results<Ok<BoardDetail>, NotFound>> GetBoard(
        Guid boardId, HttpContext http, PrikBoardDbContext db, CancellationToken ct) =>
        SnapshotAsync(boardId, http, db, withCanvasItems: true, ct);

    /// <summary>getBoard's snapshot; without <paramref name="withCanvasItems"/> its images and drawings are left out (empty).</summary>
    internal static async Task<Results<Ok<BoardDetail>, NotFound>> SnapshotAsync(
        Guid boardId, HttpContext http, PrikBoardDbContext db, bool withCanvasItems, CancellationToken ct)
    {
        IQueryable<Board> query = db.Boards.AsNoTracking().Include(b => b.Buckets).Include(b => b.Cards).Include(b => b.Tags);
        if (withCanvasItems)
        {
            query = query
                .Include(b => b.Images).ThenInclude(i => i.AddedBy)
                .Include(b => b.Images).ThenInclude(i => i.File)
                .Include(b => b.Drawings).ThenInclude(d => d.AddedBy);
        }
        var board = await query.AsSplitQuery().FirstOrDefaultAsync(b => b.Id == boardId, ct);

        if (board is null) return TypedResults.NotFound();
        var principals = await CardEndpoints.PrincipalsOnAsync(db, board.Cards, ct);
        if (http.IsBoardVisitor())
        {
            return TypedResults.Ok(BoardDetail.From(board, http.BoardRole(), principals, [], Guid.Empty).ForVisitors(board.PublicShowsPeople));
        }
        var people = await BoardAccess.PeopleAsync(db, boardId, ct);
        return TypedResults.Ok(BoardDetail.From(board, http.BoardRole(), principals, people, http.User.PrincipalId()));
    }

    private static async Task<Results<Ok<BoardDetail>, NotFound>> GetBoardByShortId(
        string shortId, HttpContext http, PrikBoardDbContext db, CancellationToken ct)
    {
        var boardId = await db.Boards.Where(b => b.ShortId == shortId).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        return boardId is { } id ? await GetBoard(id, http, db, ct) : TypedResults.NotFound();
    }

    private static async Task<Results<FileContentHttpResult, PhysicalFileHttpResult, NotFound>> GetBoardAvatar(
        Guid boardId, Guid principalId, HttpContext http, PrikBoardDbContext db, FileStore files, CancellationToken ct)
    {
        // Only people on the board, and for visitors only when the board shows them.
        if (http.IsBoardVisitor() && !await db.Boards.AnyAsync(b => b.Id == boardId && b.PublicShowsPeople, ct)) return TypedResults.NotFound();
        var onBoard = await BoardAccess.AssignableIds(db, boardId).ContainsAsync(principalId, ct)
                      || await db.Cards.AnyAsync(n => n.BoardId == boardId && (n.CreatedById == principalId || n.AssigneeIds.Contains(principalId)), ct);
        if (!onBoard) return TypedResults.NotFound();
        var file = await db.Users.AsNoTracking().Where(u => u.Id == principalId).Select(u => u.AvatarFile).FirstOrDefaultAsync(ct);
        if (file is null) return TypedResults.NotFound();
        return await StoredFileResults.ServeAsync(file, files, http.Response, ct);
    }

    private static async Task<Results<Ok<BoardSummary>, NotFound>> UpdateBoard(
        Guid boardId, UpdateBoardRequest request, HttpContext http, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        var board = await db.Boards.FindAsync([boardId], ct);
        if (board is null) return TypedResults.NotFound();

        board.Name = request.Name ?? board.Name;
        board.Key = request.Key is null ? board.Key : Board.NormalizeKey(request.Key);
        board.Description = PatchText.Apply(board.Description, request.Description);
        var rules = PatchText.Apply(board.Rules, request.Rules);
        var rulesChanged = rules != board.Rules;
        if (rulesChanged)
        {
            board.Rules = rules;
            board.RulesUpdatedAt = DateTimeOffset.UtcNow;
        }
        var visitorsChanged = (request.IsPublic is { } isPublic && isPublic != board.IsPublic)
                              || (request.PublicShowsPeople is { } shows && shows != board.PublicShowsPeople);
        board.IsPublic = request.IsPublic ?? board.IsPublic;
        board.PublicShowsPeople = request.PublicShowsPeople ?? board.PublicShowsPeople;
        board.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        // MCP caches the rules' timestamp and the board brief that agents get when they connect, which holds the rules.
        if (rulesChanged) Mcp.McpCall.ForgetRules(http.RequestServices.GetRequiredService<IMemoryCache>(), boardId);
        await events.BoardsChangedForMembersAsync(db, boardId, ct);
        // Visitors look again: gone when it is no longer public, and with or without people.
        if (visitorsChanged) await events.VisitorsChangedAsync(boardId);
        return TypedResults.Ok(BoardSummary.From(board, http.BoardRole()));
    }

    /// <summary>A short id no board has yet. The unique index still guards against two boards drawing the same one at once.</summary>
    internal static async Task<string> NewShortIdAsync(PrikBoardDbContext db, CancellationToken ct)
    {
        while (true)
        {
            var shortId = Board.NewShortId();
            if (!await db.Boards.AnyAsync(b => b.ShortId == shortId, ct)) return shortId;
        }
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBoard(
        Guid boardId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        var members = await BoardEvents.MembersOf(db, boardId, ct);
        var deleted = await db.Boards.Where(b => b.Id == boardId).ExecuteDeleteAsync(ct);
        if (deleted == 0) return TypedResults.NotFound();
        await events.BoardsChangedAsync(members);
        return TypedResults.NoContent();
    }
}
