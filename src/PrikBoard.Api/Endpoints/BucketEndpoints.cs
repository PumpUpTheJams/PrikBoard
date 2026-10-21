using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class BucketEndpoints
{
    public static RouteGroupBuilder MapBucketEndpoints(this RouteGroupBuilder api)
    {
        var buckets = api.MapGroup("/").WithTags("Buckets");

        buckets.MapPost("/boards/{boardId:guid}/buckets", CreateBucket)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createBucket")
            .WithSummary("Draw a bucket on a board")
            .WithDescription("Creates a bucket at the given rectangle (or to the right of the existing buckets), on top of " +
                             "the others. The cards and images whose centre it covers join it. Coordinates snap to the 20 px grid.")
            .ProducesValidationProblem();

        buckets.MapPatch("/buckets/{bucketId:guid}", UpdateBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("updateBucket")
            .WithSummary("Rename, recolor, move or resize a bucket")
            .WithDescription("Changes any of name, color, position and size. Omitted fields stay unchanged. " +
                             "Moving a bucket carries its cards and images along (or exactly carryCardIds and carryImageIds) " +
                             "and changes no item's bucket, also where it overlaps another bucket. A resize lets go of " +
                             "items now outside it and takes loose ones it now covers.")
            .ProducesValidationProblem();

        buckets.MapDelete("/buckets/{bucketId:guid}", DeleteBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("deleteBucket")
            .WithSummary("Delete a bucket")
            .WithDescription("Deletes the bucket. Its cards and images stay where they are and become loose " +
                             $"(or join a bucket underneath). restoreBucket brings it back within {Tombstones.RetentionText}.");

        buckets.MapPost("/buckets/{bucketId:guid}/restore", RestoreBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("restoreBucket")
            .WithSummary("Restore a deleted bucket")
            .WithDescription($"Brings back a bucket deleted in the last {Tombstones.RetentionText} with the same id, place and size, " +
                             "and the cards and images whose centre it covers join it again. 409 when it is not deleted.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    internal static async Task<Results<Created<BucketDetail>, NotFound>> CreateBucket(
        Guid boardId, CreateBucketRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var siblings = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        var (x, y) = request is { X: { } requestX, Y: { } requestY } ? (requestX, requestY) : Layout.NextBucketSpot(siblings);
        var bucket = new Bucket
        {
            BoardId = boardId,
            Name = request.Name,
            Color = request.Color ?? BucketColor.Blue,
            X = Layout.Snap(x),
            Y = Layout.Snap(y),
            Width = Layout.SnapSize(request.Width ?? Layout.DefaultBucketWidth, Layout.MinBucketWidth),
            Height = Layout.SnapSize(request.Height ?? Layout.DefaultBucketHeight, Layout.MinBucketHeight),
        };
        db.Buckets.Add(bucket);

        // Drawn around cards, images and drawings, it takes them (it is on top of the others).
        var cards = await db.Cards.Where(n => n.BoardId == boardId).ToListAsync(ct);
        var items = await CanvasItemsAsync(db, boardId, ct);
        Layout.Adopt(bucket, [.. Layout.BackToFront(siblings), bucket], cards, items, looseOnly: false);

        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/buckets/{bucket.Id}", BucketDetail.From(bucket));
    }

    internal static async Task<Results<Ok<BucketDetail>, NotFound, ValidationProblem>> UpdateBucket(
        Guid bucketId, UpdateBucketRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.FindAsync([bucketId], ct);
        if (bucket is null) return TypedResults.NotFound();

        // A new place together with a new size is a resize from a top or left corner or edge: the bucket's edge moves, its cards stay.
        var resizing = request.Width is not null || request.Height is not null;
        var moving = !resizing && request is not { X: null, Y: null };
        if (!moving && (request.CarryCardIds is not null || request.CarryImageIds is not null || request.CarryDrawingIds is not null))
        {
            var key = request.CarryCardIds is not null ? "carryCardIds" : request.CarryImageIds is not null ? "carryImageIds" : "carryDrawingIds";
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [key] = ["Only a move (X and/or Y without Width or Height) carries cards, images and drawings."],
            });
        }

        if (resizing && (request.Collapsed ?? bucket.Collapsed))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [request.Width is not null ? "width" : "height"] = ["A collapsed bucket can't be resized; open it first."],
            });
        }

        var cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        var images = await db.Images.Where(i => i.BoardId == bucket.BoardId && i.CardId == null).ToListAsync(ct);
        var drawings = await db.Drawings.Where(d => d.BoardId == bucket.BoardId && d.CardId == null).ToListAsync(ct);
        var dx = request.X is { } x ? Layout.Snap(x) - bucket.X : 0;
        var dy = request.Y is { } y ? Layout.Snap(y) - bucket.Y : 0;
        if ((dx != 0 || dy != 0) && moving)
        {
            // It carries its own cards, images and drawings, and nothing changes bucket: not its items, nor those it passes over.
            var carried = request.CarryCardIds is { } ids
                ? cards.Where(n => ids.Contains(n.Id))
                : cards.Where(n => n.BucketId == bucket.Id);
            foreach (var card in carried)
            {
                card.X = Math.Clamp(card.X + dx, Layout.MinCoordinate, Layout.MaxCoordinate);
                card.Y = Math.Clamp(card.Y + dy, Layout.MinCoordinate, Layout.MaxCoordinate);
            }
            var carriedImages = request.CarryImageIds is { } imageIds
                ? images.Where(i => imageIds.Contains(i.Id))
                : images.Where(i => i.BucketId == bucket.Id);
            foreach (var image in carriedImages)
            {
                image.X = Math.Clamp(image.X + dx, Layout.MinCoordinate, Layout.MaxCoordinate);
                image.Y = Math.Clamp(image.Y + dy, Layout.MinCoordinate, Layout.MaxCoordinate);
            }
            var carriedDrawings = request.CarryDrawingIds is { } drawingIds
                ? drawings.Where(d => drawingIds.Contains(d.Id))
                : drawings.Where(d => d.BucketId == bucket.Id);
            foreach (var drawing in carriedDrawings)
            {
                drawing.X = Math.Clamp(drawing.X + dx, Layout.MinCoordinate, Layout.MaxCoordinate);
                drawing.Y = Math.Clamp(drawing.Y + dy, Layout.MinCoordinate, Layout.MaxCoordinate);
            }
        }

        bucket.Name = request.Name ?? bucket.Name;
        bucket.Color = request.Color ?? bucket.Color;
        bucket.Collapsed = request.Collapsed ?? bucket.Collapsed;
        bucket.X += dx;
        bucket.Y += dy;
        if (request.Width is { } width) bucket.Width = Layout.SnapSize(width, Layout.MinBucketWidth);
        if (request.Height is { } height) bucket.Height = Layout.SnapSize(height, Layout.MinBucketHeight);

        if (resizing || moving)
        {
            // A new outline lets go of what is now outside it (after a move, only items left behind by carryCardIds or
            // carryImageIds or carryDrawingIds) and a resize takes loose items it now covers, never another bucket's.
            var stacked = Layout.BackToFront(await db.Buckets.Where(b => b.BoardId == bucket.BoardId).ToListAsync(ct));
            ICanvasItem[] items = [.. images, .. drawings];
            Layout.Release(bucket, stacked, cards, items);
            if (resizing) Layout.Adopt(bucket, stacked, cards, items, looseOnly: true);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBucket(
        Guid bucketId, PrikBoardDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.FindAsync([bucketId], ct);
        if (bucket is null) return TypedResults.NotFound();

        bucket.DeletedAt = DateTimeOffset.UtcNow;
        var remaining = await db.Buckets.Where(b => b.BoardId == bucket.BoardId && b.Id != bucketId).ToListAsync(ct);
        var cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        Layout.Release(bucket, Layout.BackToFront(remaining), cards, await CanvasItemsAsync(db, bucket.BoardId, ct));

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<BucketDetail>, NotFound, ProblemHttpResult>> RestoreBucket(
        Guid bucketId, PrikBoardDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).FirstOrDefaultAsync(b => b.Id == bucketId, ct);
        if (bucket is null) return TypedResults.NotFound();
        if (bucket.DeletedAt is null) return TagEndpoints.NotDeleted("bucket");

        // CreatedAt stays, so it goes back to its place in the stack and takes back the cards, images and drawings it covers.
        bucket.DeletedAt = null;
        var others = await db.Buckets.Where(b => b.BoardId == bucket.BoardId && b.Id != bucketId).ToListAsync(ct);
        var cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        Layout.Adopt(bucket, Layout.BackToFront([.. others, bucket]), cards, await CanvasItemsAsync(db, bucket.BoardId, ct), looseOnly: false);

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }

    /// <summary>The board's images and drawings: the items besides cards that buckets take, carry and let go.</summary>
    private static async Task<List<ICanvasItem>> CanvasItemsAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        [.. await db.Images.Where(i => i.BoardId == boardId).ToListAsync(ct), .. await db.Drawings.Where(d => d.BoardId == boardId).ToListAsync(ct)];
}
