using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>What images, drawings and labels share when they move between the board, a card and a bucket.</summary>
internal static class CanvasItems
{
    public static ValidationProblem? CardOrDetach(Guid? cardId, bool detach) =>
        cardId is not null && detach ? Problems.Invalid("detach", "Give either a cardId or detach, not both.") : null;

    /// <summary>Whether a request takes the item from the board to a card, between cards or back.</summary>
    public static bool ChangesCanvas(ICanvasItem item, Guid? cardId, bool detach) =>
        (cardId is { } to && to != item.CardId) || (detach && item.CardId is not null);

    /// <summary>Puts the item on the card, or back on the board; false when the card isn't on its board.</summary>
    public static async Task<bool> ChangeCanvasAsync(PrikBoardDbContext db, ICanvasItem item, Guid? cardId, bool detach, CancellationToken ct)
    {
        if (!detach && !await db.Cards.AnyAsync(n => n.Id == cardId && n.BoardId == item.BoardId, ct)) return false;
        item.CardId = detach ? null : cardId;
        return true;
    }

    /// <summary>
    /// An item that <paramref name="moved"/> joins the bucket it now sits in, or the one the pointer was in when it was dropped in the
    /// web app (<paramref name="bucketId"/>, <paramref name="loose"/>); anything else leaves it where it is.
    /// </summary>
    public static async Task<ValidationProblem?> JoinBucketAsync(PrikBoardDbContext db, ICanvasItem item, Guid? bucketId, bool? loose, bool moved, string noun, CancellationToken ct)
    {
        if (bucketId is not null || loose == true)
        {
            if (item.CardId is not null) return Problems.Invalid(bucketId is null ? "loose" : "bucketId", $"Only for {noun} on the board.");
            return BucketEndpoints.DropIn(item, bucketId, await BucketEndpoints.StackAsync(db, item.BoardId, ct));
        }
        if (moved) Layout.Place(item, await BucketEndpoints.StackAsync(db, item.BoardId, ct));
        return null;
    }

    /// <summary>Saves; an item going onto a card under the card's lock, so the card can't be deleted in between and leave it live. False when the card is gone.</summary>
    public static async Task<bool> SaveOntoAsync(PrikBoardDbContext db, Guid? cardId, Guid boardId, CancellationToken ct)
    {
        if (cardId is not { } id)
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await CardEndpoints.LockLiveCardAsync(db, id, boardId, ct)) return false;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
