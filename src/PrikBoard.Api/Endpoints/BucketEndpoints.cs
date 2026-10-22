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
                             "the others. The cards, images and drawings whose centre it covers join it. Coordinates snap to the 20 px grid.")
            .ProducesValidationProblem();

        buckets.MapPatch("/buckets/{bucketId:guid}", UpdateBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("updateBucket")
            .WithSummary("Rename, recolor, move or resize a bucket")
            .WithDescription("Changes any of name, color, position and size. Omitted fields stay unchanged. " +
                             "Moving a bucket carries its cards, images and drawings along (or exactly carryCardIds, carryImageIds " +
                             "and carryDrawingIds) " +
                             "and changes no item's bucket, also where it overlaps another bucket. A resize lets go of " +
                             "items now outside it and takes loose ones it now covers. Moving, resizing and opening it put it " +
                             "on top of the other buckets.")
            .ProducesValidationProblem();

        buckets.MapPost("/buckets/{bucketId:guid}/front", BringBucketToFront)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("bringBucketToFront")
            .WithSummary("Put a bucket on top of the others")
            .WithDescription("Puts the bucket on top of the stack, for everyone: where buckets overlap, it is drawn above the " +
                             "others and items put there join it. No item changes bucket. Moving, resizing, opening and filling a " +
                             "bucket do this too.");

        buckets.MapPost("/buckets/{bucketId:guid}/cards", PlaceCardsInBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("placeCardsInBucket")
            .WithSummary("Put several cards into a bucket")
            .WithDescription("Moves the cards into the free room of the bucket (on the 20 px grid, row by row in the order given), a " +
                             "grid step clear of the cards and images in it (also a collapsed bucket); other buckets' items don't count. " +
                             "When they don't fit, the bucket grows towards grow (Down, Right, Left or Up) by whole rows or columns, " +
                             "only as far as they need, also over neighbouring buckets (the cards still belong to this one). " +
                             "The bucket goes on top of the others. Returns the bucket and the cards.")
            .ProducesValidationProblem();

        buckets.MapDelete("/buckets/{bucketId:guid}", DeleteBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("deleteBucket")
            .WithSummary("Delete a bucket")
            .WithDescription("Deletes the bucket. Its cards, images and drawings stay where they are and become loose " +
                             $"(or join a bucket underneath). restoreBucket brings it back within {Tombstones.RetentionText}.");

        buckets.MapPost("/buckets/{bucketId:guid}/restore", RestoreBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("restoreBucket")
            .WithSummary("Restore a deleted bucket")
            .WithDescription($"Brings back a bucket deleted in the last {Tombstones.RetentionText} with the same id, place and size, " +
                             "and the cards, images and drawings whose centre it covers join it again. 409 when it is not deleted.")
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
            return Problems.Invalid(key, "Only a move (X and/or Y without Width or Height) carries cards, images and drawings.");
        }

        if (resizing && (request.Collapsed ?? bucket.Collapsed))
        {
            return Problems.Invalid(request.Width is not null ? "width" : "height", "A collapsed bucket can't be resized; open it first.");
        }

        // A new name, color or fold touches no items, so only a move or resize loads them.
        List<Card> cards = [];
        List<BoardImage> images = [];
        List<BoardDrawing> drawings = [];
        if (moving || resizing)
        {
            cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
            images = await db.Images.Where(i => i.BoardId == bucket.BoardId && i.CardId == null).ToListAsync(ct);
            drawings = await db.Drawings.Where(d => d.BoardId == bucket.BoardId && d.CardId == null).ToListAsync(ct);
        }
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
            IEnumerable<ICanvasItem> carriedItems =
            [
                .. request.CarryImageIds is { } imageIds ? images.Where(i => imageIds.Contains(i.Id)) : images.Where(i => i.BucketId == bucket.Id),
                .. request.CarryDrawingIds is { } drawingIds ? drawings.Where(d => drawingIds.Contains(d.Id)) : drawings.Where(d => d.BucketId == bucket.Id),
            ];
            foreach (var item in carriedItems)
            {
                item.X = Math.Clamp(item.X + dx, Layout.MinCoordinate, Layout.MaxCoordinate);
                item.Y = Math.Clamp(item.Y + dy, Layout.MinCoordinate, Layout.MaxCoordinate);
            }
        }

        // What someone moves, resizes or opens comes to the top, for everyone (before the stack below decides what it lets go).
        if (resizing || moving || (bucket.Collapsed && request.Collapsed == false)) bucket.ToFront();
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

    internal static async Task<Results<Ok<BucketDetail>, NotFound>> BringBucketToFront(
        Guid bucketId, PrikBoardDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.FindAsync([bucketId], ct);
        if (bucket is null) return TypedResults.NotFound();

        bucket.ToFront();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }

    internal static async Task<Results<Ok<PlacedCards>, NotFound, ValidationProblem>> PlaceCardsInBucket(
        Guid bucketId, PlaceCardsRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        // One at a time per bucket, so two drops into it don't take the same free slots.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var bucket = await db.Buckets.FromSql($"""SELECT * FROM "Buckets" WHERE "Id" = {bucketId} FOR UPDATE""").FirstOrDefaultAsync(ct);
        if (bucket is null) return TypedResults.NotFound();

        var ids = request.CardIds.Distinct().ToList();
        var boardCards = await db.Cards.Where(n => n.BoardId == bucket.BoardId).ToDictionaryAsync(n => n.Id, ct);
        if (!ids.TrueForAll(boardCards.ContainsKey))
        {
            return Problems.Invalid("cardIds", "Every card must be on the bucket's board.");
        }
        var cards = ids.ConvertAll(id => boardCards[id]);
        // It may grow over its neighbours, so it comes on top of them, with the cards it takes.
        bucket.ToFront();

        var buckets = await db.Buckets.Where(b => b.BoardId == bucket.BoardId).ToListAsync(ct);
        var images = await db.Images.Where(i => i.BoardId == bucket.BoardId && i.CardId == null).ToListAsync(ct);
        var picked = ids.ToHashSet();
        var others = boardCards.Values.Where(n => !picked.Contains(n.Id)).ToList();
        // They join the bucket by id, so only its own items (and loose ones) are in the way, not its neighbours': it may
        // grow over those, only as far as needed.
        bool InTheWay(Guid? owner) => owner is null || owner == bucket.Id;
        List<(int X, int Y, int Width, int Height)> taken =
        [
            .. others.Where(n => InTheWay(n.BucketId)).Select(n => (n.X, n.Y, Layout.CardWidth, Layout.CardHeight)),
            .. images.Where(i => InTheWay(i.BucketId)).Select(i => (i.X, i.Y, i.Width, i.Height)),
        ];
        var spots = Layout.PackIn(bucket, cards.Count, taken, request.Grow ?? GrowSide.Down);

        var now = DateTimeOffset.UtcNow;
        foreach (var (card, (x, y)) in cards.Zip(spots))
        {
            (card.X, card.Y) = (x, y);
            // Asked for by name, so they join even a collapsed bucket (which other cards don't).
            card.BucketId = bucket.Id;
            card.UpdatedAt = now;
        }
        // A bucket that grew can now cover loose items too.
        var drawings = await db.Drawings.Where(d => d.BoardId == bucket.BoardId && d.CardId == null).ToListAsync(ct);
        Layout.Adopt(bucket, Layout.BackToFront(buckets), others, [.. images, .. drawings], looseOnly: true);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var board = (await CardEndpoints.BoardRefAsync(db, bucket.BoardId, ct))!;
        var principals = await CardEndpoints.PrincipalsOnAsync(db, cards, ct);
        return TypedResults.Ok(new PlacedCards(BucketDetail.From(bucket), [.. cards.Select(n => CardDetail.From(n, board, principals))]));
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
        var bucket = await db.Buckets.WithDeleted().FirstOrDefaultAsync(b => b.Id == bucketId, ct);
        if (bucket is null) return TypedResults.NotFound();
        if (bucket.DeletedAt is null) return Problems.NotDeleted("bucket");

        // StackedAt stays, so it goes back to its place in the stack and takes back the cards, images and drawings it covers.
        bucket.DeletedAt = null;
        var others = await db.Buckets.Where(b => b.BoardId == bucket.BoardId && b.Id != bucketId).ToListAsync(ct);
        var cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        Layout.Adopt(bucket, Layout.BackToFront([.. others, bucket]), cards, await CanvasItemsAsync(db, bucket.BoardId, ct), looseOnly: false);

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }

    /// <summary>The images and drawings on the board (not on cards): the items besides cards that buckets take, carry and let go.</summary>
    internal static async Task<List<ICanvasItem>> CanvasItemsAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
    [
        .. await db.Images.Where(i => i.BoardId == boardId && i.CardId == null).ToListAsync(ct),
        .. await db.Drawings.Where(d => d.BoardId == boardId && d.CardId == null).ToListAsync(ct),
    ];
}
