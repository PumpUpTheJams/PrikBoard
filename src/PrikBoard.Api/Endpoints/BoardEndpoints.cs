using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class BoardEndpoints
{
    public static RouteGroupBuilder MapBoardEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Boards");

        boards.MapGet("/", ListBoards)
            .WithName("listBoards")
            .WithSummary("List boards")
            .WithDescription("Returns every board without its columns and notes, newest first.");

        boards.MapPost("/", CreateBoard)
            .WithName("createBoard")
            .WithSummary("Create a board")
            .WithDescription("Creates a board with the default columns \"To do\", \"Doing\" and \"Done\".")
            .ProducesValidationProblem();

        boards.MapGet("/{boardId:guid}", GetBoard)
            .WithName("getBoard")
            .WithSummary("Get a full board snapshot")
            .WithDescription("Returns the board with all columns and notes in display order. " +
                             "This is the best single call to understand the current state of a project.");

        boards.MapPatch("/{boardId:guid}", UpdateBoard)
            .WithName("updateBoard")
            .WithSummary("Update a board")
            .WithDescription("Changes the board name and/or description. Omitted fields stay unchanged.")
            .ProducesValidationProblem();

        boards.MapDelete("/{boardId:guid}", DeleteBoard)
            .WithName("deleteBoard")
            .WithSummary("Delete a board")
            .WithDescription("Permanently deletes the board including all of its columns and notes.");

        return api;
    }

    private static async Task<Ok<List<BoardSummary>>> ListBoards(PrikBoardDbContext db, CancellationToken ct)
    {
        var boards = await db.Boards.AsNoTracking().OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(boards.Select(BoardSummary.From).ToList());
    }

    private static async Task<Created<BoardDetail>> CreateBoard(
        CreateBoardRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var board = new Board { Name = request.Name, Description = PatchText.Apply(null, request.Description) };
        board.Columns.AddRange(Board.DefaultColumnNames.Select((name, i) => new Column { Name = name, Position = i }));

        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/boards/{board.Id}", BoardDetail.From(board));
    }

    private static async Task<Results<Ok<BoardDetail>, NotFound>> GetBoard(
        Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking()
            .Include(b => b.Columns).ThenInclude(c => c.Notes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);

        return board is null ? TypedResults.NotFound() : TypedResults.Ok(BoardDetail.From(board));
    }

    private static async Task<Results<Ok<BoardSummary>, NotFound>> UpdateBoard(
        Guid boardId, UpdateBoardRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var board = await db.Boards.FindAsync([boardId], ct);
        if (board is null) return TypedResults.NotFound();

        board.Name = request.Name ?? board.Name;
        board.Description = PatchText.Apply(board.Description, request.Description);
        board.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BoardSummary.From(board));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBoard(
        Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var deleted = await db.Boards.Where(b => b.Id == boardId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }
}
