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
            .WithSummary("Create a bucket")
            .WithDescription("Creates a bucket at the given rectangle (or to the right of the existing buckets), on top of " +
                             "the others. The loose cards, images and drawings whose center it covers join it (items in another " +
                             "bucket stay there); it is the only change to a bucket that takes items. Coordinates snap to the 20 px grid.")
            .ProducesValidationProblem();

        buckets.MapPatch("/buckets/{bucketId:guid}", UpdateBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("updateBucket")
            .WithSummary("Rename, move, resize or fold a bucket")
            .WithDescription("Changes any of name, collapsed, position and size. Omitted fields stay unchanged. " +
                             "An x or y sent with a size is a resize from a top or left corner or edge. " +
                             "Moving a bucket carries its cards, images and drawings along (or exactly carryCardIds, carryImageIds " +
                             "and carryDrawingIds, which only a move takes) and changes no item's bucket, even where it overlaps " +
                             "another bucket. A resize lets go of items that no longer overlap it (they go loose) and takes none; a " +
                             "collapsed bucket can't be resized (400). Moving, resizing and opening a bucket put it on top of the others.")
            .ProducesValidationProblem();

        buckets.MapPost("/buckets/{bucketId:guid}/front", BringBucketToFront)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("bringBucketToFront")
            .WithSummary("Bring a bucket to the front")
            .WithDescription("Puts the bucket on top of the stack, for everyone: where buckets overlap, it is drawn above the " +
                             "others and items put there join it. No item changes bucket. Moving, resizing, opening and filling a " +
                             "bucket do this too.");

        buckets.MapPost("/buckets/{bucketId:guid}/items", PlaceItemsInBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("placeItemsInBucket")
            .WithSummary("Put cards, images and drawings into a bucket")
            .WithDescription("Moves the items into free room in the bucket, each at its own size: cards first, then images, then " +
                             "drawings, row by row in the order given, on the 20 px grid and a grid step clear of the bucket's own and " +
                             "loose items (only cards with onlyCardsTakeRoom); other buckets' items don't count. When they don't fit, the bucket grows towards grow (Down by " +
                             "default, or Right, Left or Up) by whole rows or columns, only as far as needed, also over neighboring " +
                             "buckets (the items still join this one; loose items it grows over stay loose). Works on a collapsed bucket too. The bucket goes on top of the " +
                             "others. Returns the bucket and the items; 400 when no item is given, or one is not on the bucket's board " +
                             "itself.")
            .ProducesValidationProblem();

        buckets.MapDelete("/buckets/{bucketId:guid}", DeleteBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("deleteBucket")
            .WithSummary("Delete a bucket")
            .WithDescription("Deletes the bucket. Its cards, images and drawings stay where they are and become loose, even " +
                             $"over another bucket. restoreBucket brings it back within {Tombstones.RetentionText}.");

        buckets.MapPost("/buckets/{bucketId:guid}/restore", RestoreBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("restoreBucket")
            .WithSummary("Restore a deleted bucket")
            .WithDescription($"Brings back a bucket deleted in the last {Tombstones.RetentionText} with the same id, place and size. " +
                             "It takes no items: put its old ones back with moveCard, updateImage or updateDrawing (bucketId with " +
                             "their x and y). 409 when it is not deleted.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    internal static async Task<Results<Created<BucketDetail>, NotFound>> CreateBucket(
        Guid boardId, CreateBucketRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        // The board exists: the role check (or the MCP tool's) found it.

        var siblings = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        var (x, y) = request is { X: { } requestX, Y: { } requestY } ? (requestX, requestY) : Layout.NextBucketSpot(siblings);
        var bucket = new Bucket
        {
            BoardId = boardId,
            Name = request.Name,
            X = Layout.Snap(x),
            Y = Layout.Snap(y),
            Width = Layout.SnapSize(request.Width ?? Layout.DefaultBucketWidth, Layout.MinBucketWidth),
            Height = Layout.SnapSize(request.Height ?? Layout.DefaultBucketHeight, Layout.MinBucketHeight),
        };
        db.Buckets.Add(bucket);

        // Drawn around loose cards, images and drawings, it takes them (it is on top of the others). Items already in a
        // bucket stay in theirs.
        var cards = await db.Cards.Where(n => n.BoardId == boardId).ToListAsync(ct);
        var items = await CanvasItemsAsync(db, boardId, ct);
        Layout.Adopt(bucket, [.. Layout.BackToFront(siblings), bucket], cards, items);

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
        if (!moving && (request.CarryCardIds is not null || request.CarryImageIds is not null || request.CarryDrawingIds is not null || request.CarryLabelIds is not null))
        {
            var key = request.CarryCardIds is not null ? "carryCardIds"
                : request.CarryImageIds is not null ? "carryImageIds"
                : request.CarryDrawingIds is not null ? "carryDrawingIds"
                : "carryLabelIds";
            return Problems.Invalid(key, "Only a move (X and/or Y without Width or Height) carries cards, images, drawings and labels.");
        }

        if (resizing && (request.Collapsed ?? bucket.Collapsed))
        {
            return Problems.Invalid(request.Width is not null ? "width" : "height", "A collapsed bucket can't be resized; open it first.");
        }

        var dx = request.X is { } x ? Layout.Snap(x) - bucket.X : 0;
        var dy = request.Y is { } y ? Layout.Snap(y) - bucket.Y : 0;
        // A plain move carries all its items, so none is left behind to let go, and none needs loading.
        var carriesAll = moving && request is { CarryCardIds: null, CarryImageIds: null, CarryDrawingIds: null, CarryLabelIds: null };
        await using var transaction = carriesAll ? await db.Database.BeginTransactionAsync(ct) : null;
        if (carriesAll && (dx != 0 || dy != 0)) await CarryAllAsync(db, bucket.Id, dx, dy, ct);

        // A new name, color or fold touches no items, and the others only touch the bucket's own and those named to carry.
        List<Card> cards = [];
        List<BoardImage> images = [];
        List<BoardDrawing> drawings = [];
        List<BoardLabel> labels = [];
        if ((moving || resizing) && !carriesAll)
        {
            List<Guid> carryCards = request.CarryCardIds ?? [], carryImages = request.CarryImageIds ?? [], carryDrawings = request.CarryDrawingIds ?? [], carryLabels = request.CarryLabelIds ?? [];
            cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId && (n.BucketId == bucket.Id || carryCards.Contains(n.Id))).ToListAsync(ct);
            images = await db.Images.Where(i => i.BoardId == bucket.BoardId && i.CardId == null && (i.BucketId == bucket.Id || carryImages.Contains(i.Id))).ToListAsync(ct);
            drawings = await db.Drawings.Where(d => d.BoardId == bucket.BoardId && d.CardId == null && (d.BucketId == bucket.Id || carryDrawings.Contains(d.Id))).ToListAsync(ct);
            labels = await db.Labels.Where(l => l.BoardId == bucket.BoardId && l.CardId == null && (l.BucketId == bucket.Id || carryLabels.Contains(l.Id))).ToListAsync(ct);
        }
        if ((dx != 0 || dy != 0) && moving && !carriesAll)
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
                .. request.CarryLabelIds is { } labelIds ? labels.Where(l => labelIds.Contains(l.Id)) : labels.Where(l => l.BucketId == bucket.Id),
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
        bucket.Collapsed = request.Collapsed ?? bucket.Collapsed;
        bucket.X += dx;
        bucket.Y += dy;
        if (request.Width is { } width) bucket.Width = Layout.SnapSize(width, Layout.MinBucketWidth);
        if (request.Height is { } height) bucket.Height = Layout.SnapSize(height, Layout.MinBucketHeight);

        if ((resizing || moving) && !carriesAll)
        {
            // A new outline lets go of what no longer reaches into it (after a move, only items left behind by carryCardIds
            // or carryImageIds or carryDrawingIds), and takes nothing: only a bucket drawn around items takes them.
            var stacked = await StackAsync(db, bucket.BoardId, ct);
            ICanvasItem[] items = [.. images, .. drawings, .. labels];
            Layout.Release(bucket, stacked, cards, items);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }

    /// <summary>Moves everything in a bucket along with it, in one statement per kind of item. Moves within a bucket aren't in a card's history.</summary>
    private static async Task CarryAllAsync(PrikBoardDbContext db, Guid bucketId, int dx, int dy, CancellationToken ct)
    {
        const int low = Layout.MinCoordinate, high = Layout.MaxCoordinate;
        await db.Cards.Where(n => n.BucketId == bucketId).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.X, n => Math.Min(Math.Max(n.X + dx, low), high))
            .SetProperty(n => n.Y, n => Math.Min(Math.Max(n.Y + dy, low), high)), ct);
        await db.Images.Where(i => i.BucketId == bucketId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.X, i => Math.Min(Math.Max(i.X + dx, low), high))
            .SetProperty(i => i.Y, i => Math.Min(Math.Max(i.Y + dy, low), high)), ct);
        await db.Drawings.Where(d => d.BucketId == bucketId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.X, d => Math.Min(Math.Max(d.X + dx, low), high))
            .SetProperty(d => d.Y, d => Math.Min(Math.Max(d.Y + dy, low), high)), ct);
        await db.Labels.Where(l => l.BucketId == bucketId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.X, l => Math.Min(Math.Max(l.X + dx, low), high))
            .SetProperty(l => l.Y, l => Math.Min(Math.Max(l.Y + dy, low), high)), ct);
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

    internal static async Task<Results<Ok<PlacedItems>, NotFound, ValidationProblem>> PlaceItemsInBucket(
        Guid bucketId, PlaceItemsRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        // One at a time per bucket, so two drops into it don't take the same free room.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var bucket = await db.Buckets.FromSql($"""SELECT * FROM "Buckets" WHERE "Id" = {bucketId} FOR UPDATE""").FirstOrDefaultAsync(ct);
        if (bucket is null) return TypedResults.NotFound();

        var cardIds = (request.CardIds ?? []).Distinct().ToList();
        var imageIds = (request.ImageIds ?? []).Distinct().ToList();
        var drawingIds = (request.DrawingIds ?? []).Distinct().ToList();
        var labelIds = (request.LabelIds ?? []).Distinct().ToList();
        if (cardIds.Count + imageIds.Count + drawingIds.Count + labelIds.Count == 0)
        {
            return Problems.Invalid("cardIds", "Name at least one card, image, drawing or label to put in the bucket.");
        }

        var cards = await db.Cards.Where(n => n.BoardId == bucket.BoardId && cardIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        if (cards.Count != cardIds.Count) return Problems.Invalid("cardIds", "Every card must be on the bucket's board.");
        var images = await db.Images.Include(i => i.File).Include(i => i.AddedBy)
            .Where(i => i.BoardId == bucket.BoardId && i.CardId == null && imageIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        if (images.Count != imageIds.Count) return Problems.Invalid("imageIds", "Every image must be on the bucket's board itself, not on a card.");
        var drawings = await db.Drawings.Include(d => d.AddedBy)
            .Where(d => d.BoardId == bucket.BoardId && d.CardId == null && drawingIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);
        if (drawings.Count != drawingIds.Count) return Problems.Invalid("drawingIds", "Every drawing must be on the bucket's board itself, not on a card.");

        var labels = await db.Labels.Include(l => l.AddedBy)
            .Where(l => l.BoardId == bucket.BoardId && l.CardId == null && labelIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);
        if (labels.Count != labelIds.Count) return Problems.Invalid("labelIds", "Every label must be on the bucket's board itself, not on a card.");

        var items = new BucketItems(cardIds.ConvertAll(id => cards[id]), imageIds.ConvertAll(id => images[id]), drawingIds.ConvertAll(id => drawings[id]), labelIds.ConvertAll(id => labels[id]));
        var buckets = await db.Buckets.Where(b => b.BoardId == bucket.BoardId).ToListAsync(ct);
        // The board's other cards only by place and bucket: with all their content they would be loaded and compared on
        // save for nothing. The ones the grown bucket takes are saved with their new bucket.
        var others = (await CardEndpoints.PlacedCardsAsync(db, bucket.BoardId, null, ct)).Where(n => !cards.ContainsKey(n.Id)).ToList();
        var was = others.ToDictionary(n => n.Id, n => n.BucketId);
        await PackItemsAsync(db, bucket, buckets, others, items, request.Grow ?? GrowSide.Down, ct, cardsOnly: request.OnlyCardsTakeRoom == true);
        CardEndpoints.TrackMoved(db, others, was);
        var now = DateTimeOffset.UtcNow;
        foreach (var card in items.Cards) card.UpdatedAt = now;
        foreach (var image in items.Images) image.UpdatedAt = now;
        foreach (var drawing in items.Drawings) drawing.UpdatedAt = now;
        foreach (var label in items.Labels ?? []) label.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var board = (await CardEndpoints.BoardRefAsync(db, bucket.BoardId, ct))!;
        var principals = await CardEndpoints.PrincipalsOnAsync(db, items.Cards, ct);
        return TypedResults.Ok(new PlacedItems(
            BucketDetail.From(bucket),
            [.. items.Cards.Select(n => CardDetail.From(n, board, principals))],
            [.. items.Images.Select(ImageDetail.From)],
            [.. items.Drawings.Select(DrawingDetail.From)],
            [.. (items.Labels ?? []).Select(LabelDetail.From)]));
    }

    /// <summary>The cards, images, drawings and labels going into a bucket, each list in the order they fill its free room.</summary>
    internal sealed record BucketItems(List<Card> Cards, List<BoardImage> Images, List<BoardDrawing> Drawings, List<BoardLabel>? Labels = null);

    /// <summary>
    /// Puts <paramref name="items"/> into free room in <paramref name="bucket"/>, each at its own size and cards first
    /// (<see cref="Layout.PackIn"/>), and brings it to the front; <paramref name="others"/> are the board's other cards.
    /// With <paramref name="cardsOnly"/>, the images, drawings and labels already there take no room. placeItemsInBucket and
    /// pastes into a bucket use this.
    /// </summary>
    internal static async Task PackItemsAsync(
        PrikBoardDbContext db, Bucket bucket, List<Bucket> buckets, List<Card> others, BucketItems items, GrowSide grow, CancellationToken ct,
        bool cardsOnly = false)
    {
        // It may grow over its neighbors, so it comes on top of them, with the items it takes.
        bucket.ToFront();
        var moving = items.Images.Select(i => i.Id).Concat(items.Drawings.Select(d => d.Id)).Concat((items.Labels ?? []).Select(l => l.Id)).ToHashSet();
        var images = await db.Images.Where(i => i.BoardId == bucket.BoardId && i.CardId == null).ToListAsync(ct);
        var drawings = await db.Drawings.Where(d => d.BoardId == bucket.BoardId && d.CardId == null).ToListAsync(ct);
        var labels = await db.Labels.Where(l => l.BoardId == bucket.BoardId && l.CardId == null).ToListAsync(ct);
        List<ICanvasItem> staying = [.. images.Where(i => !moving.Contains(i.Id)), .. drawings.Where(d => !moving.Contains(d.Id)), .. labels.Where(l => !moving.Contains(l.Id))];
        // They join the bucket by id, so only its own items (and loose ones) are in the way, not its neighbors': it may
        // grow over those, only as far as needed.
        bool InTheWay(Guid? owner) => owner is null || owner == bucket.Id;
        List<(int X, int Y, int Width, int Height)> taken =
        [
            .. others.Where(n => InTheWay(n.BucketId)).Select(n => (n.X, n.Y, Layout.CardWidth, Layout.CardHeight)),
            .. staying.Where(i => !cardsOnly && InTheWay(i.BucketId)).Select(i => (i.X, i.Y, i.Width, i.Height)),
        ];
        List<ICanvasItem> placed = [.. items.Images, .. items.Drawings, .. items.Labels ?? []];
        List<(int Width, int Height)> sizes =
        [
            .. items.Cards.Select(_ => (Layout.CardWidth, Layout.CardHeight)),
            .. placed.Select(i => (i.Width, i.Height)),
        ];
        var spots = Layout.PackIn(bucket, sizes, taken, grow);
        // Put in this bucket on purpose, so they join it even when it is collapsed (which other items don't).
        foreach (var (card, (x, y)) in items.Cards.Zip(spots))
        {
            (card.X, card.Y) = (x, y);
            card.BucketId = bucket.Id;
        }
        foreach (var (item, (x, y)) in placed.Zip(spots.Skip(items.Cards.Count)))
        {
            (item.X, item.Y) = (x, y);
            item.BucketId = bucket.Id;
        }
        // A bucket that grew takes nothing else: loose items it now covers stay loose (only a bucket drawn around them takes them).
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

        // StackedAt stays, so it goes back to its place in the stack. It takes no items: its own went loose when it was
        // deleted, and the web app's undo puts them back in it one by one.
        bucket.DeletedAt = null;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }

    /// <summary>The board's buckets, back to front: what decides which bucket a card, image or drawing joins.</summary>
    internal static async Task<List<Bucket>> StackAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        Layout.BackToFront(await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct));

    /// <summary>
    /// An item dropped in the web app goes by the pointer: into the bucket it names (the one the pointer was in), which it
    /// must overlap (<see cref="Layout.PlaceIn(ICanvasItem, Bucket)"/>), or loose when it names none. A problem keyed on
    /// bucketId otherwise.
    /// </summary>
    internal static ValidationProblem? DropIn(ICanvasItem item, Guid? bucketId, IReadOnlyList<Bucket> buckets)
    {
        if (bucketId is not { } id)
        {
            item.BucketId = null;
            return null;
        }
        if (buckets.FirstOrDefault(b => b.Id == id) is not { } bucket) return Problems.Invalid("bucketId", "The bucket does not exist on this board.");
        return Layout.PlaceIn(item, bucket) ? null : Problems.Invalid("bucketId", "With X and Y, the item must overlap the bucket.");
    }

    /// <summary>Likewise for a card (<see cref="Layout.PlaceIn(Card, Bucket)"/>).</summary>
    internal static ValidationProblem? DropIn(Card card, Guid? bucketId, IReadOnlyList<Bucket> buckets)
    {
        if (bucketId is not { } id)
        {
            card.BucketId = null;
            return null;
        }
        if (buckets.FirstOrDefault(b => b.Id == id) is not { } bucket) return Problems.Invalid("bucketId", "The bucket does not exist on this board.");
        return Layout.PlaceIn(card, bucket) ? null : Problems.Invalid("bucketId", "With X and Y, the card must overlap the bucket.");
    }

    /// <summary>The images, drawings and labels on the board (not on cards): the items besides cards that buckets take, carry and let go.</summary>
    internal static async Task<List<ICanvasItem>> CanvasItemsAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
    [
        .. await db.Images.Where(i => i.BoardId == boardId && i.CardId == null).ToListAsync(ct),
        .. await db.Drawings.Where(d => d.BoardId == boardId && d.CardId == null).ToListAsync(ct),
        .. await db.Labels.Where(l => l.BoardId == boardId && l.CardId == null).ToListAsync(ct),
    ];
}
