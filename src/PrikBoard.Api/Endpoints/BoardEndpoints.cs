using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
            .WithDescription("Returns the boards you are a member of, without their buckets and notes, newest first, with your role on each.");

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
            .WithName("getBoard")
            .WithSummary("Get a full board snapshot")
            .WithDescription("Returns the board with every bucket and note and where they sit on the canvas. " +
                             "This is the best single call to understand the current state of a project.");

        boards.MapPatch("/{boardId:guid}", UpdateBoard)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("updateBoard")
            .WithSummary("Update a board")
            .WithDescription("Changes the board name, description and/or agent rules. Omitted fields stay unchanged. Only owners can " +
                             "do this, so agents cannot change the rules they follow.")
            .ProducesValidationProblem();

        boards.MapDelete("/{boardId:guid}", DeleteBoard)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("deleteBoard")
            .WithSummary("Delete a board")
            .WithDescription("Permanently deletes the board including all of its buckets, notes, images, members and invites.");

        return api;
    }

    internal static async Task<Ok<List<BoardSummary>>> ListBoards(ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        // A personal access token lists its owner's boards, with its capped role and only the boards it is limited to.
        var caller = BoardCaller.Of(principal);
        var memberships = await db.BoardMembers.AsNoTracking()
            .Where(m => m.PrincipalId == caller.MemberId)
            .Include(m => m.Board)
            .OrderByDescending(m => m.Board!.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(memberships
            .Select(m => (m.Board, Role: caller.Apply(m.BoardId, m.Role)))
            .Where(m => m.Role is not null)
            .Select(m => BoardSummary.From(m.Board!, m.Role!.Value))
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

        var board = new Board { Name = request.Name, Description = PatchText.Apply(null, request.Description) };
        foreach (var (name, color) in Board.DefaultBuckets)
        {
            var (x, y) = Layout.NextBucketSpot(board.Buckets);
            board.Buckets.Add(new Bucket
            {
                Name = name,
                Color = color,
                X = x,
                Y = y,
                Width = Layout.DefaultBucketWidth,
                Height = Layout.DefaultBucketHeight,
                // Distinct timestamps keep the stacking order of the defaults well defined.
                CreatedAt = board.CreatedAt.AddMilliseconds(board.Buckets.Count),
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

    internal static async Task<Results<Ok<BoardDetail>, NotFound>> GetBoard(
        Guid boardId, HttpContext http, PrikBoardDbContext db, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking()
            .Include(b => b.Buckets)
            .Include(b => b.Notes)
            .Include(b => b.Images).ThenInclude(i => i.AddedBy)
            .Include(b => b.Images).ThenInclude(i => i.File)
            .Include(b => b.Drawings).ThenInclude(d => d.AddedBy)
            .Include(b => b.Tags)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);

        if (board is null) return TypedResults.NotFound();
        var principals = await NoteEndpoints.PrincipalsOnAsync(db, board.Notes, ct);
        var people = await BoardAccess.PeopleAsync(db, boardId, ct);
        return TypedResults.Ok(BoardDetail.From(board, http.BoardRole(), principals, people, http.User.PrincipalId()));
    }

    private static async Task<Results<Ok<BoardSummary>, NotFound>> UpdateBoard(
        Guid boardId, UpdateBoardRequest request, HttpContext http, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        var board = await db.Boards.FindAsync([boardId], ct);
        if (board is null) return TypedResults.NotFound();

        board.Name = request.Name ?? board.Name;
        board.Description = PatchText.Apply(board.Description, request.Description);
        var rules = PatchText.Apply(board.Rules, request.Rules);
        if (rules != board.Rules)
        {
            board.Rules = rules;
            board.RulesUpdatedAt = DateTimeOffset.UtcNow;
        }
        board.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await events.BoardsChangedForMembersAsync(db, boardId, ct);
        return TypedResults.Ok(BoardSummary.From(board, http.BoardRole()));
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
