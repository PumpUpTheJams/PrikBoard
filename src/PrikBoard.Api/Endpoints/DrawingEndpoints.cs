using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class DrawingEndpoints
{
    public static RouteGroupBuilder MapDrawingEndpoints(this RouteGroupBuilder api)
    {
        var drawings = api.MapGroup("/").WithTags("Drawings");

        drawings.MapPost("/boards/{boardId:guid}/drawings", CreateDrawing)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createDrawing")
            .WithSummary("Draw a freehand stroke")
            .WithDescription("Adds one pen stroke to the board, or to a card's own canvas with cardId. Send its top-left x and y " +
                             "(snapped to the 20 px grid), the points as [x, y, pressure] relative to that corner (not snapped), an ink " +
                             "color and a base stroke width of 2, 4 or 8. The response gives the box the stroke fills; the points " +
                             $"come back from getDrawingContent. A board holds at most {BoardDrawing.MaxPerBoard} drawings (409 beyond that).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        drawings.MapGet("/drawings/{drawingId:guid}/content", GetDrawingContent)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Drawing)
            .WithName("getDrawingContent")
            .WithSummary("Get a drawing's points")
            .WithDescription("Returns the stroke's points as [x, y, pressure], relative to the drawing's x and y. " +
                             "A drawing's points never change, so they can be cached.");

        drawings.MapPatch("/drawings/{drawingId:guid}", UpdateDrawing)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("updateDrawing")
            .WithSummary("Move or recolor a drawing")
            .WithDescription("Changes its position or ink, puts it on a card's canvas (cardId, with x and y on that canvas), or " +
                             "puts a drawing on a card back on the board (detach, with x and y on the board). Omitted fields stay unchanged.")
            .ProducesValidationProblem();

        drawings.MapDelete("/drawings/{drawingId:guid}", DeleteDrawing)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("deleteDrawing")
            .WithSummary("Delete a drawing")
            .WithDescription($"Deletes the drawing. restoreDrawing brings it back within {Tombstones.RetentionText}.");

        drawings.MapPost("/drawings/{drawingId:guid}/restore", RestoreDrawing)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("restoreDrawing")
            .WithSummary("Restore a deleted drawing")
            .WithDescription($"Brings back a drawing deleted in the last {Tombstones.RetentionText}, with the same id, place and points. " +
                             "409 when it is not deleted, or when it was on a card that is deleted (restore the card instead).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    internal static async Task<Results<Created<DrawingDetail>, NotFound, ValidationProblem, ProblemHttpResult>> CreateDrawing(
        Guid boardId, CreateDrawingRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var card = request.CardId is null ? null : await db.Cards.FirstOrDefaultAsync(n => n.Id == request.CardId && n.BoardId == boardId, ct);
        if (request.CardId is not null && card is null) return Invalid("cardId", "The card does not exist on this board.");

        var drawing = new BoardDrawing
        {
            BoardId = boardId,
            CardId = card?.Id,
            Color = request.Color,
            StrokeWidth = request.StrokeWidth,
            AddedBy = await db.Principals.FindAsync([principal.PrincipalId()], ct),
        };
        if (Build(drawing, request.Points, request.X!.Value, request.Y!.Value, "") is { } invalid) return invalid;
        Layout.Place(drawing, await ImageEndpoints.StackAsync(db, boardId, ct));

        if (await db.Drawings.CountAsync(d => d.BoardId == boardId, ct) >= BoardDrawing.MaxPerBoard)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many drawings",
                detail: $"A board holds at most {BoardDrawing.MaxPerBoard} drawings. Delete some first.");
        }

        db.Drawings.Add(drawing);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/drawings/{drawing.Id}", DrawingDetail.From(drawing));
    }

    /// <summary>
    /// Checks and sets <paramref name="drawing"/>'s points (with the size that follows) and places it at x and y: snapped
    /// on the board, or snapped and kept on the canvas when it is on a card. Errors are keyed <c>{prefix}points[i]</c>.
    /// </summary>
    internal static ValidationProblem? Build(BoardDrawing drawing, IReadOnlyList<double[]> points, int x, int y, string prefix)
    {
        if (!BoardDrawing.StrokeWidths.Contains(drawing.StrokeWidth))
        {
            return Invalid($"{prefix}strokeWidth", "The stroke width is 2, 4 or 8.");
        }
        if (points.Count is 0 or > BoardDrawing.MaxPoints)
        {
            return Invalid($"{prefix}points", $"A drawing has 1 to {BoardDrawing.MaxPoints} points.");
        }
        if (drawing.SetPoints(points) is { } bad) return Invalid($"{prefix}points[{bad.Index}]", bad.Message);
        Place(drawing, x, y);
        return null;
    }

    /// <summary>Snaps x and y; on a card, also keeps the drawing on its canvas, at the last grid line that fits.</summary>
    private static void Place(BoardDrawing drawing, int x, int y) =>
        (drawing.X, drawing.Y) = drawing.CardId is null
            ? (Layout.Snap(x), Layout.Snap(y))
            : (OnCard(x, drawing.Width), OnCard(y, drawing.Height));

    private static int OnCard(int value, int size) =>
        Math.Clamp(Layout.Snap(value), 0, Math.Max(0, (Layout.CardCanvasMax - size) / Layout.Grid * Layout.Grid));

    private static async Task<Results<Ok<DrawingContentDetail>, NotFound>> GetDrawingContent(
        Guid drawingId, HttpResponse response, PrikBoardDbContext db, CancellationToken ct)
    {
        var points = await db.Drawings.AsNoTracking()
            .Where(d => d.Id == drawingId)
            .Select(d => d.Content!.Points)
            .FirstOrDefaultAsync(ct);
        if (points is null) return TypedResults.NotFound();

        // Points never change for an id, and every request is authenticated, so private caching is safe.
        response.Headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";
        return TypedResults.Ok(new DrawingContentDetail(drawingId, points));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ValidationProblem>> UpdateDrawing(
        Guid drawingId, UpdateDrawingRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.Include(d => d.AddedBy).FirstOrDefaultAsync(d => d.Id == drawingId, ct);
        if (drawing is null) return TypedResults.NotFound();

        var detach = request.Detach == true;
        if (request.CardId is not null && detach) return Invalid("detach", "Give either a cardId or detach, not both.");
        var moving = (request.CardId is { } to && to != drawing.CardId) || (detach && drawing.CardId is not null);
        if (moving && (request.X is null || request.Y is null))
        {
            return Invalid(request.X is null ? "x" : "y", "Give x and y on the new canvas when moving between the board and a card.");
        }
        if (moving && !detach)
        {
            if (!await db.Cards.AnyAsync(n => n.Id == request.CardId && n.BoardId == drawing.BoardId, ct))
            {
                return Invalid("cardId", "The card does not exist on this board.");
            }
            drawing.CardId = request.CardId;
        }
        else if (moving)
        {
            drawing.CardId = null;
        }

        Place(drawing, request.X ?? drawing.X, request.Y ?? drawing.Y);
        // A drawing that moved joins the bucket it now sits in; only a new color leaves it where it is.
        if (moving || request.X is not null || request.Y is not null)
        {
            Layout.Place(drawing, await ImageEndpoints.StackAsync(db, drawing.BoardId, ct));
        }
        drawing.Color = request.Color ?? drawing.Color;
        drawing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteDrawing(Guid drawingId, PrikBoardDbContext db, CancellationToken ct)
    {
        var deleted = await db.Drawings.Where(d => d.Id == drawingId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, DateTimeOffset.UtcNow), ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ProblemHttpResult>> RestoreDrawing(
        Guid drawingId, PrikBoardDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Include(d => d.AddedBy)
            .FirstOrDefaultAsync(d => d.Id == drawingId, ct);
        if (drawing is null) return TypedResults.NotFound();
        if (drawing.DeletedAt is null) return TagEndpoints.NotDeleted("drawing");
        if (drawing.CardId is { } cardId && !await db.Cards.AnyAsync(n => n.Id == cardId, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Card deleted",
                detail: "The drawing is on a deleted card. Restore the card (restoreCard) to bring back its drawings.");
        }

        drawing.DeletedAt = null;
        Layout.Recheck(drawing, await ImageEndpoints.StackAsync(db, drawing.BoardId, ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
