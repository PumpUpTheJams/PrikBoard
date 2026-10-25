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
            .WithSummary("Start a freehand drawing")
            .WithDescription("Adds a drawing of one pen stroke to the board, or to a card's own canvas with cardId; add more strokes " +
                             "to it with addDrawingStroke, so a sketch selects, moves and deletes as one. Send its top-left x and y " +
                             "(snapped to the 20 px grid), the points as [x, y, pressure] relative to that corner (not snapped), an ink " +
                             "color and a base stroke width of 2, 4 or 8. The response gives the box the stroke fills; the points " +
                             $"come back from getDrawingContent. A board holds at most {BoardDrawing.MaxPerBoard} drawings (409 beyond that).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        drawings.MapGet("/drawings/{drawingId:guid}/content", GetDrawingContent)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Drawing)
            .AllowVisitors()
            .WithName("getDrawingContent")
            .WithSummary("Get a drawing's strokes")
            .WithDescription("Returns the drawing's strokes in drawing order, each with its base width and its points as [x, y, pressure], " +
                             "relative to the drawing's x and y. Their inks are the drawing's colors. Fetch it from the drawing's contentUrl, " +
                             "which changes whenever the strokes do, so a response can be cached.");

        drawings.MapPost("/drawings/{drawingId:guid}/strokes", AddDrawingStroke)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("addDrawingStroke")
            .WithSummary("Add a stroke to a drawing")
            .WithDescription("Adds a pen stroke to a drawing, which grows to hold it. Send the stroke's own top-left x and y on the " +
                             "drawing's canvas (the board, or the card's canvas), snapped to the 20 px grid, and its points relative to that, " +
                             $"as in createDrawing. A drawing holds at most {BoardDrawing.MaxStrokes} strokes and {BoardDrawing.MaxTotalPoints} points, " +
                             $"and reaches at most {BoardDrawing.MaxSize} px from its corner (409 beyond that).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        drawings.MapDelete("/drawings/{drawingId:guid}/strokes/{index:int}", DeleteDrawingStroke)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("deleteDrawingStroke")
            .WithSummary("Take a stroke off a drawing")
            .WithDescription("Removes the stroke at index (0 is the first drawn) and shrinks the drawing to the strokes left. " +
                             "404 when there is no such stroke; 409 for the only stroke (delete the drawing instead).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        drawings.MapPost("/drawings/{drawingId:guid}/transform", TransformDrawing)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("transformDrawing")
            .WithSummary("Scale or rotate a drawing")
            .WithDescription("Scales a drawing (scaleX, scaleY) and then turns it clockwise (rotation, in degrees), both about a pivot " +
                             "on its canvas (pivotX, pivotY; by default the middle of its box). Its points change and its box follows " +
                             "them; line widths stay. On the board it joins the bucket containing its new center, and it goes on top of the stack. " +
                             $"409 when it would reach more than {BoardDrawing.MaxSize} px from its corner or leave the board.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        drawings.MapPut("/drawings/{drawingId:guid}/strokes", SetDrawingStrokes)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("setDrawingStrokes")
            .WithSummary("Replace a drawing's strokes")
            .WithDescription("Writes new points for every stroke of a drawing, with its new top-left x and y (snapped to the grid), " +
                             "for example to put it back as it was before transformDrawing. Give as many strokes as it has, in drawing " +
                             "order; their inks stay. Points are relative to x and y, as in createDrawing.")
            .ProducesValidationProblem();

        drawings.MapPatch("/drawings/{drawingId:guid}", UpdateDrawing)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("updateDrawing")
            .WithSummary("Move or recolor a drawing")
            .WithDescription("Changes its position or ink (color for all its strokes, or colors for each), puts it on a card's canvas (cardId, with x and y on that canvas), or " +
                             "puts a drawing on a card back on the board (detach, with x and y on the board). Omitted fields stay unchanged. " +
                             "A drawing that moves goes on top of the stack (see stackedAt).")
            .ProducesValidationProblem();

        drawings.MapPost("/drawings/{drawingId:guid}/front", BringDrawingToFront)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Drawing)
            .WithName("bringDrawingToFront")
            .WithSummary("Bring a drawing to the front")
            .WithDescription("Puts a loose drawing on top of the stack it shares with the buckets, for everyone: it is drawn above " +
                             "them until one of them comes to the front again (see stackedAt). Drawing, moving and reshaping it do " +
                             "this too. A drawing in a bucket or on a card is drawn with its bucket or card, so this changes nothing " +
                             "you see for it until it is loose.");

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
        // The board exists: the role check (or the MCP tool's) found it.

        if (request.CardId is not null && !await db.Cards.AnyAsync(n => n.Id == request.CardId && n.BoardId == boardId, ct))
        {
            return Problems.CardNotOnBoard();
        }

        var drawing = new BoardDrawing
        {
            BoardId = boardId,
            CardId = request.CardId,
            AddedBy = await db.Principals.FindAsync([principal.PrincipalId()], ct),
        };
        if (Build(drawing, [(request.Color, request.StrokeWidth, request.Points)], request.X!.Value, request.Y!.Value, "") is { } invalid) return invalid;
        Layout.Place(drawing, await BucketEndpoints.StackAsync(db, boardId, ct));

        if (await db.Drawings.CountAsync(d => d.BoardId == boardId, ct) >= BoardDrawing.MaxPerBoard)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many drawings",
                detail: $"A board holds at most {BoardDrawing.MaxPerBoard} drawings. Delete some first.");
        }

        // On a card: saved under its lock, so the card can't be deleted in between and leave this drawing live.
        await using var transaction = drawing.CardId is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (drawing.CardId is { } cardId && !await CardEndpoints.LockLiveCardAsync(db, cardId, boardId, ct)) return Problems.CardNotOnBoard();
        db.Drawings.Add(drawing);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/drawings/{drawing.Id}", DrawingDetail.From(drawing));
    }

    /// <summary>
    /// Checks and sets <paramref name="drawing"/>'s strokes (with the size that follows), their points relative to x and y,
    /// and places it there: snapped on the board, or snapped and kept on the canvas when it is on a card. Errors are keyed
    /// <c>{prefix}points[i]</c>, or <c>{prefix}strokes[s].points[i]</c> for more than one stroke.
    /// </summary>
    internal static ValidationProblem? Build(
        BoardDrawing drawing, IReadOnlyList<(InkColor Color, int Width, IReadOnlyList<double[]> Points)> strokes, int x, int y, string prefix)
    {
        if (strokes.Count is 0 or > BoardDrawing.MaxStrokes)
        {
            return Problems.Invalid($"{prefix}strokes", $"A drawing has 1 to {BoardDrawing.MaxStrokes} strokes.");
        }
        var checkedStrokes = new List<DrawingStroke>();
        foreach (var (i, stroke) in strokes.Index())
        {
            var (ok, problem) = CheckStroke(stroke.Width, stroke.Points, strokes.Count == 1 ? prefix : $"{prefix}strokes[{i}].");
            if (problem is not null) return problem;
            checkedStrokes.Add(ok!);
        }
        if (checkedStrokes.Sum(s => s.Points.Length) > BoardDrawing.MaxTotalPoints)
        {
            return Problems.Invalid($"{prefix}strokes", $"A drawing has at most {BoardDrawing.MaxTotalPoints} points.");
        }
        drawing.SetStrokes(checkedStrokes, [.. strokes.Select(s => s.Color)]);
        Place(drawing, x, y);
        return null;
    }

    /// <summary>Checks one stroke's width and points; errors are keyed <c>{prefix}strokeWidth</c> and <c>{prefix}points[i]</c>.</summary>
    private static (DrawingStroke? Stroke, ValidationProblem? Problem) CheckStroke(int width, IReadOnlyList<double[]> points, string prefix)
    {
        if (!BoardDrawing.StrokeWidths.Contains(width)) return (null, Problems.Invalid($"{prefix}strokeWidth", "The stroke width is 2, 4 or 8."));
        if (points.Count is 0 or > BoardDrawing.MaxPoints)
        {
            return (null, Problems.Invalid($"{prefix}points", $"A stroke has 1 to {BoardDrawing.MaxPoints} points."));
        }
        var (rounded, bad) = BoardDrawing.CheckPoints(points);
        return bad is { } b ? (null, Problems.Invalid($"{prefix}points[{b.Index}]", b.Message)) : (new DrawingStroke(width, rounded!), null);
    }

    /// <summary>Snaps x and y; on a card, also keeps the drawing on its canvas, at the last grid line that fits.</summary>
    private static void Place(BoardDrawing drawing, int x, int y) =>
        (drawing.X, drawing.Y) = drawing.CardId is null
            ? (Layout.Snap(x), Layout.Snap(y))
            : (OnCard(x, drawing.Width), OnCard(y, drawing.Height));

    private static int OnCard(int value, int size) =>
        Math.Clamp(Layout.Snap(value), 0, Math.Max(0, (Layout.CardCanvasMax - size) / Layout.Grid * Layout.Grid));

    private static async Task<Results<Ok<DrawingContentDetail>, NotFound>> GetDrawingContent(
        Guid drawingId, int? v, HttpResponse response, PrikBoardDbContext db, CancellationToken ct)
    {
        var content = await db.Drawings.AsNoTracking()
            .Where(d => d.Id == drawingId)
            .Select(d => new { d.Revision, d.Content!.Strokes })
            .FirstOrDefaultAsync(ct);
        if (content is null) return TypedResults.NotFound();

        // contentUrl names the revision, so that URL always gets these strokes and can be cached (privately: access is
        // checked on every request). Any other URL, for an old revision or none, has to ask again.
        response.Headers[HeaderNames.CacheControl] = v == content.Revision ? "private, max-age=31536000, immutable" : "private, no-cache";
        return TypedResults.Ok(DrawingContentDetail.From(drawingId, content.Strokes));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ValidationProblem>> UpdateDrawing(
        Guid drawingId, UpdateDrawingRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.Include(d => d.AddedBy).FirstOrDefaultAsync(d => d.Id == drawingId, ct);
        if (drawing is null) return TypedResults.NotFound();

        var detach = request.Detach == true;
        if (request.CardId is not null && detach) return Problems.Invalid("detach", "Give either a cardId or detach, not both.");
        if (request.Color is not null && request.Colors is not null) return Problems.Invalid("colors", "Give either a color or colors, not both.");
        if (request.Colors is { } colors && colors.Count != drawing.Colors.Count)
        {
            return Problems.Invalid("colors", $"The drawing has {drawing.Colors.Count} strokes; give an ink for each.");
        }
        var moving = (request.CardId is { } to && to != drawing.CardId) || (detach && drawing.CardId is not null);
        if (moving && (request.X is null || request.Y is null))
        {
            return Problems.Invalid(request.X is null ? "x" : "y", "Give x and y on the new canvas when moving between the board and a card.");
        }
        if (moving && !detach)
        {
            if (!await db.Cards.AnyAsync(n => n.Id == request.CardId && n.BoardId == drawing.BoardId, ct))
            {
                return Problems.CardNotOnBoard();
            }
            drawing.CardId = request.CardId;
        }
        else if (moving)
        {
            drawing.CardId = null;
        }

        Place(drawing, request.X ?? drawing.X, request.Y ?? drawing.Y);
        // A drawing that moved joins the bucket it now sits in, on top; only a new color leaves it where it is.
        if (moving || request.X is not null || request.Y is not null)
        {
            Layout.Place(drawing, await BucketEndpoints.StackAsync(db, drawing.BoardId, ct));
            drawing.ToFront();
        }
        if (request.Color is { } color) drawing.Colors = [.. drawing.Colors.Select(_ => color)];
        if (request.Colors is { } inks) drawing.Colors = [.. inks];
        drawing.UpdatedAt = DateTimeOffset.UtcNow;

        // Onto a card: saved under its lock, so the card can't be deleted in between and leave this drawing live.
        var onto = moving ? drawing.CardId : null;
        await using var transaction = onto is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (onto is { } cardId && !await CardEndpoints.LockLiveCardAsync(db, cardId, drawing.BoardId, ct)) return Problems.CardNotOnBoard();
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ValidationProblem, ProblemHttpResult>> AddDrawingStroke(
        Guid drawingId, AddDrawingStrokeRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var (stroke, invalid) = CheckStroke(request.StrokeWidth, request.Points, "");
        if (invalid is not null) return invalid;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedDrawingAsync(db, drawingId, ct) is not { } drawing) return TypedResults.NotFound();
        if (drawing.Colors.Count >= BoardDrawing.MaxStrokes || drawing.PointCount + stroke!.Points.Length > BoardDrawing.MaxTotalPoints)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Drawing full",
                detail: $"A drawing holds at most {BoardDrawing.MaxStrokes} strokes and {BoardDrawing.MaxTotalPoints} points. Start a new drawing.");
        }
        if (!drawing.AddStroke(stroke, request.Color, Layout.Snap(request.X!.Value), Layout.Snap(request.Y!.Value)))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Drawing too big",
                detail: $"A drawing reaches at most {BoardDrawing.MaxSize} px from its corner. Start a new drawing.");
        }
        await SettleAsync(db, drawing, ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ProblemHttpResult>> DeleteDrawingStroke(
        Guid drawingId, int index, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedDrawingAsync(db, drawingId, ct) is not { } drawing) return TypedResults.NotFound();
        if (index < 0 || index >= drawing.Colors.Count) return TypedResults.NotFound();
        if (drawing.Colors.Count == 1)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Only stroke",
                detail: "This is the drawing's only stroke. Delete the drawing (deleteDrawing) instead.");
        }

        drawing.RemoveStroke(index);
        await SettleAsync(db, drawing, ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ValidationProblem, ProblemHttpResult>> TransformDrawing(
        Guid drawingId, TransformDrawingRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        if (request.PivotX is null != request.PivotY is null)
        {
            return Problems.Invalid(request.PivotX is null ? "pivotX" : "pivotY", "Give pivotX and pivotY together, or neither.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedDrawingAsync(db, drawingId, ct) is not { } drawing) return TypedResults.NotFound();
        var pivotX = request.PivotX ?? drawing.X + drawing.Width / 2.0;
        var pivotY = request.PivotY ?? drawing.Y + drawing.Height / 2.0;
        if (drawing.Transform(request.ScaleX, request.ScaleY, request.Rotation, pivotX, pivotY) is { } problem)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Drawing too big", detail: problem);
        }
        await SettleAsync(db, drawing, ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ValidationProblem>> SetDrawingStrokes(
        Guid drawingId, SetDrawingStrokesRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedDrawingAsync(db, drawingId, ct) is not { } drawing) return TypedResults.NotFound();
        if (request.Strokes.Count != drawing.Colors.Count)
        {
            return Problems.Invalid("strokes", $"The drawing has {drawing.Colors.Count} strokes; give the points of each.");
        }
        var strokes = request.Strokes.Zip(drawing.Colors, (s, color) => (color, s.StrokeWidth, (IReadOnlyList<double[]>)s.Points)).ToList();
        if (Build(drawing, strokes, request.X!.Value, request.Y!.Value, "") is { } invalid) return invalid;
        drawing.Revision++;
        await SettleAsync(db, drawing, ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound>> BringDrawingToFront(
        Guid drawingId, PrikBoardDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.Include(d => d.AddedBy).FirstOrDefaultAsync(d => d.Id == drawingId, ct);
        if (drawing is null) return TypedResults.NotFound();

        drawing.ToFront();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }

    /// <summary>
    /// After its strokes changed: keeps the drawing on its card's canvas, lets it join the bucket it now sits in and puts
    /// it on top (like a drawing that moved), and saves.
    /// </summary>
    private static async Task SettleAsync(PrikBoardDbContext db, BoardDrawing drawing, CancellationToken ct)
    {
        Place(drawing, drawing.X, drawing.Y);
        Layout.Place(drawing, await BucketEndpoints.StackAsync(db, drawing.BoardId, ct));
        drawing.ToFront();
        drawing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Locks the drawing's row until the transaction ends, then loads it with its strokes, so strokes added at the same
    /// time both stay; null when it does not exist.
    /// </summary>
    private static Task<BoardDrawing?> LockedDrawingAsync(PrikBoardDbContext db, Guid drawingId, CancellationToken ct) =>
        db.Drawings.FromSql($"""SELECT * FROM "Drawings" WHERE "Id" = {drawingId} FOR UPDATE""")
            .Include(d => d.AddedBy).Include(d => d.Content).FirstOrDefaultAsync(ct);

    internal static async Task<Results<NoContent, NotFound>> DeleteDrawing(
        Guid drawingId, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.AsNoTracking().Where(d => d.Id == drawingId).Select(d => new { d.BoardId, d.CardId }).FirstOrDefaultAsync(ct);
        var deleted = await db.Drawings.Where(d => d.Id == drawingId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, DateTimeOffset.UtcNow), ct);
        if (deleted == 0) return TypedResults.NotFound();
        if (drawing?.CardId is { } cardId)
        {
            CardHistory.Record(db, principal, cardId, drawing.BoardId, new(CardChangeKind.DrawingRemoved, Ref: drawingId));
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<DrawingDetail>, NotFound, ProblemHttpResult>> RestoreDrawing(
        Guid drawingId, PrikBoardDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.WithDeleted()
            .Include(d => d.AddedBy)
            .FirstOrDefaultAsync(d => d.Id == drawingId, ct);
        if (drawing is null) return TypedResults.NotFound();
        if (drawing.DeletedAt is null) return Problems.NotDeleted("drawing");
        if (drawing.CardId is { } cardId && !await db.Cards.AnyAsync(n => n.Id == cardId, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Card deleted",
                detail: "The drawing is on a deleted card. Restore the card (restoreCard) to bring back its drawings.");
        }

        drawing.DeletedAt = null;
        Layout.Recheck(drawing, await BucketEndpoints.StackAsync(db, drawing.BoardId, ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DrawingDetail.From(drawing));
    }
}
