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
            .WithDescription("Creates a board with the default buckets \"To do\", \"Doing\" and \"Done\" side by side. You become its owner.")
            .ProducesValidationProblem();

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
            .WithDescription("Changes the board name and/or description. Omitted fields stay unchanged.")
            .ProducesValidationProblem();

        boards.MapDelete("/{boardId:guid}", DeleteBoard)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("deleteBoard")
            .WithSummary("Delete a board")
            .WithDescription("Permanently deletes the board including all of its buckets, notes, images, members and invites.");

        return api;
    }

    private static async Task<Ok<List<BoardSummary>>> ListBoards(ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var principalId = principal.PrincipalId();
        var memberships = await db.BoardMembers.AsNoTracking()
            .Where(m => m.PrincipalId == principalId)
            .Include(m => m.Board)
            .OrderByDescending(m => m.Board!.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(memberships.Select(m => BoardSummary.From(m.Board!, m.Role)).ToList());
    }

    private static async Task<Created<BoardDetail>> CreateBoard(
        CreateBoardRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
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

        board.Members.Add(new BoardMember { PrincipalId = principal.PrincipalId(), Role = BoardRole.Owner });

        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/boards/{board.Id}", BoardDetail.From(board, BoardRole.Owner));
    }

    private static async Task<Results<Ok<BoardDetail>, NotFound>> GetBoard(
        Guid boardId, HttpContext http, PrikBoardDbContext db, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking()
            .Include(b => b.Buckets)
            .Include(b => b.Notes)
            .Include(b => b.Images).ThenInclude(i => i.AddedBy)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);

        return board is null ? TypedResults.NotFound() : TypedResults.Ok(BoardDetail.From(board, http.BoardRole()));
    }

    private static async Task<Results<Ok<BoardSummary>, NotFound>> UpdateBoard(
        Guid boardId, UpdateBoardRequest request, HttpContext http, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        var board = await db.Boards.FindAsync([boardId], ct);
        if (board is null) return TypedResults.NotFound();

        board.Name = request.Name ?? board.Name;
        board.Description = PatchText.Apply(board.Description, request.Description);
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
