using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// Deleting and restoring many items of a board in one request, as deleteCard, deleteBucket, deleteImage and deleteDrawing
/// (and their restores) do one by one: a whole selection, or undoing a big paste, is one transaction and one change for
/// everyone watching the board.
/// </summary>
public static class ItemEndpoints
{
    public static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder api)
    {
        var boards = api.MapGroup("/boards").WithTags("Cards");

        boards.MapPost("/{boardId:guid}/items/delete", DeleteItems)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("deleteItems")
            .WithSummary("Delete several items")
            .WithDescription("Deletes cards (with their images and drawings), buckets, images and drawings of the board at once, as " +
                             "deleteCard, deleteBucket, deleteImage and deleteDrawing do: what is in a deleted bucket stays on the board, " +
                             $"loose, and a card with nothing on it goes for good. restoreItems brings the rest back within {Tombstones.RetentionText}. " +
                             "Every item must be on the board and not deleted (400 otherwise, and nothing is deleted).")
            .ProducesValidationProblem();

        boards.MapPost("/{boardId:guid}/items/restore", RestoreItems)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("restoreItems")
            .WithSummary("Restore several deleted items")
            .WithDescription($"Brings back cards, buckets, images and drawings of the board deleted in the last {Tombstones.RetentionText}, " +
                             "as restoreCard, restoreBucket, restoreImage and restoreDrawing do: each with the same id at the place it had, " +
                             "in its bucket when that is back and still reaches it, a card with the images and drawings deleted along with it. " +
                             "Items that aren't deleted stay as they are. 400 when an item is not on the board or gone for good, 409 when " +
                             "an image or drawing is on a deleted card that isn't restored with it.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteItems(
        Guid boardId, ItemIds request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var (cardIds, bucketIds, imageIds, drawingIds) = Distinct(request);
        if (cardIds.Count + bucketIds.Count + imageIds.Count + drawingIds.Count == 0) return Nothing();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var cards = await db.Cards.AsNoTracking().Where(n => n.BoardId == boardId && cardIds.Contains(n.Id)).ToListAsync(ct);
        var buckets = await db.Buckets.Where(b => b.BoardId == boardId && bucketIds.Contains(b.Id)).ToListAsync(ct);
        var images = await db.Images.AsNoTracking().Where(i => i.BoardId == boardId && imageIds.Contains(i.Id))
            .Select(i => new { i.Id, i.CardId, Label = i.Caption ?? i.File!.FileName }).ToListAsync(ct);
        var drawings = await db.Drawings.AsNoTracking().Where(d => d.BoardId == boardId && drawingIds.Contains(d.Id))
            .Select(d => new { d.Id, d.CardId }).ToListAsync(ct);
        if (Missing(cards.Count, cardIds, buckets.Count, bucketIds, images.Count, imageIds, drawings.Count, drawingIds, "not deleted already") is { } missing)
        {
            return missing;
        }

        // Images and drawings first, so those on the cards going too come back with them only when asked for.
        await db.Images.Where(i => imageIds.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, now), ct);
        await db.Drawings.Where(d => drawingIds.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, now), ct);
        foreach (var image in images.Where(i => i.CardId is not null))
        {
            CardHistory.Record(db, principal, image.CardId!.Value, boardId, new(CardChangeKind.ImageRemoved, Before: image.Label, Ref: image.Id));
        }
        foreach (var drawing in drawings.Where(d => d.CardId is not null))
        {
            CardHistory.Record(db, principal, drawing.CardId!.Value, boardId, new(CardChangeKind.DrawingRemoved, Ref: drawing.Id));
        }

        // A card with nothing on it goes for good, as deleteCard does; images and drawings deleted from it earlier count.
        var attached = await db.Images.WithDeleted().Where(i => i.CardId != null && cardIds.Contains(i.CardId.Value)).Select(i => i.CardId!.Value)
            .Concat(db.Drawings.WithDeleted().Where(d => d.CardId != null && cardIds.Contains(d.CardId.Value)).Select(d => d.CardId!.Value))
            .Distinct().ToListAsync(ct);
        var blank = cards.Where(n => n.IsBlank() && !attached.Contains(n.Id)).Select(n => n.Id).ToList();
        if (blank.Count > 0) await db.Cards.Where(n => blank.Contains(n.Id)).ExecuteDeleteAsync(ct);
        var trashed = cards.Where(n => !blank.Contains(n.Id)).Select(n => n.Id).ToList();
        await Tombstones.DeleteCardsAsync(db, trashed, now, ct);
        foreach (var id in trashed) CardHistory.Record(db, principal, id, boardId, new(CardChangeKind.Deleted));

        // Buckets last, as deleteBucket: what else is in them stays, loose.
        if (buckets.Count > 0)
        {
            foreach (var bucket in buckets) bucket.DeletedAt = now;
            var remaining = Layout.BackToFront(await db.Buckets.Where(b => b.BoardId == boardId && !bucketIds.Contains(b.Id)).ToListAsync(ct));
            var left = await db.Cards.Where(n => n.BoardId == boardId).ToListAsync(ct);
            var items = await BucketEndpoints.CanvasItemsAsync(db, boardId, ct);
            foreach (var bucket in buckets) Layout.Release(bucket, remaining, left, items);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RestoredItems>, NotFound, ValidationProblem, ProblemHttpResult>> RestoreItems(
        Guid boardId, ItemIds request, PrikBoardDbContext db, CancellationToken ct)
    {
        var (cardIds, bucketIds, imageIds, drawingIds) = Distinct(request);
        if (cardIds.Count + bucketIds.Count + imageIds.Count + drawingIds.Count == 0) return Nothing();
        if (await CardEndpoints.BoardRefAsync(db, boardId, ct) is not { } boardRef) return TypedResults.NotFound();

        // Tags are renamed and deleted under the board's lock, on deleted cards too, so the cards read after it have current ones.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var buckets = await db.Buckets.WithDeleted().Where(b => b.BoardId == boardId && bucketIds.Contains(b.Id)).ToListAsync(ct);
        var cards = await db.Cards.WithDeleted().Where(n => n.BoardId == boardId && cardIds.Contains(n.Id)).ToListAsync(ct);
        var images = await db.Images.WithDeleted().Include(i => i.AddedBy).Include(i => i.File)
            .Where(i => i.BoardId == boardId && imageIds.Contains(i.Id)).ToListAsync(ct);
        var drawings = await db.Drawings.WithDeleted().Include(d => d.AddedBy)
            .Where(d => d.BoardId == boardId && drawingIds.Contains(d.Id)).ToListAsync(ct);
        if (Missing(cards.Count, cardIds, buckets.Count, bucketIds, images.Count, imageIds, drawings.Count, drawingIds, "not gone for good") is { } missing)
        {
            return missing;
        }

        // An image or drawing on a card comes back only with the card.
        var deletedCards = cards.Where(n => n.DeletedAt is not null).ToList();
        var cardsOn = images.Where(i => i.DeletedAt is not null).Select(i => i.CardId)
            .Concat(drawings.Where(d => d.DeletedAt is not null).Select(d => d.CardId))
            .OfType<Guid>().Distinct().ToList();
        var live = await db.Cards.Where(n => cardsOn.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct);
        if (cardsOn.Except(live).Except(deletedCards.Select(n => n.Id)).Any())
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Card deleted",
                detail: "An image or drawing is on a deleted card. Restore the card with it (cardIds) to bring it back.");
        }

        // Buckets first, so the items in them can join them again. Like restoreBucket, they take nothing themselves.
        foreach (var bucket in buckets) bucket.DeletedAt = null;
        var stacked = Layout.BackToFront([.. (await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct)).UnionBy(buckets, b => b.Id)]);

        // Tags deleted meanwhile were taken off the cards as well, so this only fills a gap.
        if (await TagEndpoints.EnsureBoardTagsAsync(db, boardId, [.. deletedCards.SelectMany(n => n.Tags).Distinct()], ct) is { } tooManyTags)
        {
            return tooManyTags;
        }
        // Cards deleted together share a stamp, and so do the images and drawings deleted with them.
        foreach (var group in deletedCards.GroupBy(n => n.DeletedAt!.Value))
        {
            var ids = group.Select(n => n.Id).ToList();
            var stamp = group.Key;
            await db.Images.WithDeleted().Where(i => i.CardId != null && ids.Contains(i.CardId.Value) && i.DeletedAt == stamp)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, (DateTimeOffset?)null), ct);
            await db.Drawings.WithDeleted().Where(d => d.CardId != null && ids.Contains(d.CardId.Value) && d.DeletedAt == stamp)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, (DateTimeOffset?)null), ct);
        }
        // UpdatedAt stays, so they go back to their places in the stacking order too, and back in their bucket if that still
        // reaches them, else loose.
        foreach (var card in deletedCards)
        {
            card.DeletedAt = null;
            Layout.Recheck(card, stacked);
        }
        foreach (var image in images.Where(i => i.DeletedAt is not null))
        {
            image.DeletedAt = null;
            Layout.Recheck(image, stacked);
        }
        foreach (var drawing in drawings.Where(d => d.DeletedAt is not null))
        {
            drawing.DeletedAt = null;
            Layout.Recheck(drawing, stacked);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var principals = await CardEndpoints.PrincipalsOnAsync(db, cards, ct);
        return TypedResults.Ok(new RestoredItems(
            [.. cards.Select(n => CardDetail.From(n, boardRef, principals))],
            [.. buckets.Select(BucketDetail.From)],
            [.. images.Select(ImageDetail.From)],
            [.. drawings.Select(DrawingDetail.From)]));
    }

    private static (List<Guid> Cards, List<Guid> Buckets, List<Guid> Images, List<Guid> Drawings) Distinct(ItemIds request) => (
        [.. (request.CardIds ?? []).Distinct()],
        [.. (request.BucketIds ?? []).Distinct()],
        [.. (request.ImageIds ?? []).Distinct()],
        [.. (request.DrawingIds ?? []).Distinct()]);

    private static ValidationProblem Nothing() => Problems.Invalid("cardIds", "Name at least one card, bucket, image or drawing.");

    /// <summary>A problem keyed on the first list with an item that wasn't found, or null when all were.</summary>
    private static ValidationProblem? Missing(
        int cards, List<Guid> cardIds, int buckets, List<Guid> bucketIds, int images, List<Guid> imageIds, int drawings, List<Guid> drawingIds,
        string condition)
    {
        var key = cards != cardIds.Count ? "cardIds"
            : buckets != bucketIds.Count ? "bucketIds"
            : images != imageIds.Count ? "imageIds"
            : drawings != drawingIds.Count ? "drawingIds"
            : null;
        return key is null ? null : Problems.Invalid(key, $"Every item must be on this board and {condition}.");
    }
}
