using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class ColumnEndpoints
{
    public static RouteGroupBuilder MapColumnEndpoints(this RouteGroupBuilder api)
    {
        var columns = api.MapGroup("/").WithTags("Columns");

        columns.MapPost("/boards/{boardId:guid}/columns", CreateColumn)
            .WithName("createColumn")
            .WithSummary("Add a column to a board")
            .WithDescription("Inserts a column at the given position (or at the end) and shifts the others.")
            .ProducesValidationProblem();

        columns.MapPatch("/columns/{columnId:guid}", UpdateColumn)
            .WithName("updateColumn")
            .WithSummary("Rename or reorder a column")
            .WithDescription("Changes the column name and/or position. Omitted fields stay unchanged.")
            .ProducesValidationProblem();

        columns.MapDelete("/columns/{columnId:guid}", DeleteColumn)
            .WithName("deleteColumn")
            .WithSummary("Delete an empty column")
            .WithDescription("Deletes the column. Fails with 409 Conflict while it still holds notes; move or delete them first.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<Results<Created<ColumnDetail>, NotFound>> CreateColumn(
        Guid boardId, CreateColumnRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var siblings = await db.Columns.Where(c => c.BoardId == boardId).ToListAsync(ct);
        var column = new Column { BoardId = boardId, Name = request.Name };
        Positioning.Place(siblings, column, request.Position);

        db.Columns.Add(column);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/columns/{column.Id}", ColumnDetail.From(column));
    }

    private static async Task<Results<Ok<ColumnDetail>, NotFound>> UpdateColumn(
        Guid columnId, UpdateColumnRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var column = await db.Columns.Include(c => c.Notes).FirstOrDefaultAsync(c => c.Id == columnId, ct);
        if (column is null) return TypedResults.NotFound();

        column.Name = request.Name ?? column.Name;
        if (request.Position is { } position)
        {
            var siblings = await db.Columns.Where(c => c.BoardId == column.BoardId).ToListAsync(ct);
            Positioning.Place(siblings, column, position);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ColumnDetail.From(column));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteColumn(
        Guid columnId, PrikBoardDbContext db, CancellationToken ct)
    {
        var column = await db.Columns.FindAsync([columnId], ct);
        if (column is null) return TypedResults.NotFound();

        if (await db.Notes.AnyAsync(n => n.ColumnId == columnId, ct))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Column is not empty",
                detail: "Move or delete the notes in this column before deleting it.");
        }

        db.Columns.Remove(column);
        var remaining = await db.Columns.Where(c => c.BoardId == column.BoardId && c.Id != columnId).ToListAsync(ct);
        Positioning.Renumber(remaining);

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
