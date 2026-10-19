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
            .WithDescription("Creates a bucket at the given rectangle (or to the right of the existing buckets). " +
                             "Loose notes inside it join it. Coordinates snap to the 20 px grid.")
            .ProducesValidationProblem();

        buckets.MapPatch("/buckets/{bucketId:guid}", UpdateBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("updateBucket")
            .WithSummary("Rename, recolour, move or resize a bucket")
            .WithDescription("Changes any of name, colour, position and size. Omitted fields stay unchanged. " +
                             "Moving a bucket carries its notes along (or exactly carryNoteIds).")
            .ProducesValidationProblem();

        buckets.MapDelete("/buckets/{bucketId:guid}", DeleteBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("deleteBucket")
            .WithSummary("Delete a bucket")
            .WithDescription("Deletes the bucket. Its notes stay where they are and become loose " +
                             $"(or join a bucket underneath). restoreBucket brings it back within {Tombstones.RetentionText}.");

        buckets.MapPost("/buckets/{bucketId:guid}/restore", RestoreBucket)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Bucket)
            .WithName("restoreBucket")
            .WithSummary("Restore a deleted bucket")
            .WithDescription($"Brings back a bucket deleted in the last {Tombstones.RetentionText} with the same id, place and size, " +
                             "and the notes whose centre it covers join it again. 409 when it is not deleted.")
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

        var notes = await db.Notes.Where(n => n.BoardId == boardId).ToListAsync(ct);
        Layout.AssignBuckets(notes, [.. siblings, bucket]);

        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/buckets/{bucket.Id}", BucketDetail.From(bucket));
    }

    internal static async Task<Results<Ok<BucketDetail>, NotFound, ValidationProblem>> UpdateBucket(
        Guid bucketId, UpdateBucketRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.FindAsync([bucketId], ct);
        if (bucket is null) return TypedResults.NotFound();

        // A new place together with a new size is a resize from a top or left corner or edge: the frame's edge moves, its notes stay.
        var resizing = request.Width is not null || request.Height is not null;
        if (request.CarryNoteIds is not null && (resizing || request is { X: null, Y: null }))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["carryNoteIds"] = ["Only a move (X and/or Y without Width or Height) carries notes."],
            });
        }

        var notes = await db.Notes.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        var dx = request.X is { } x ? Layout.Snap(x) - bucket.X : 0;
        var dy = request.Y is { } y ? Layout.Snap(y) - bucket.Y : 0;
        if ((dx != 0 || dy != 0) && !resizing)
        {
            var carried = request.CarryNoteIds is { } ids
                ? notes.Where(n => ids.Contains(n.Id))
                : notes.Where(n => n.BucketId == bucket.Id);
            foreach (var note in carried)
            {
                note.X = Math.Max(0, note.X + dx);
                note.Y = Math.Max(0, note.Y + dy);
            }
        }

        bucket.Name = request.Name ?? bucket.Name;
        bucket.Color = request.Color ?? bucket.Color;
        bucket.X += dx;
        bucket.Y += dy;
        if (request.Width is { } width) bucket.Width = Layout.SnapSize(width, Layout.MinBucketWidth);
        if (request.Height is { } height) bucket.Height = Layout.SnapSize(height, Layout.MinBucketHeight);

        var buckets = await db.Buckets.Where(b => b.BoardId == bucket.BoardId).ToListAsync(ct);
        Layout.AssignBuckets(notes, buckets);

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
        var notes = await db.Notes.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        Layout.AssignBuckets(notes, remaining);

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<BucketDetail>, NotFound, ProblemHttpResult>> RestoreBucket(
        Guid bucketId, PrikBoardDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).FirstOrDefaultAsync(b => b.Id == bucketId, ct);
        if (bucket is null) return TypedResults.NotFound();
        if (bucket.DeletedAt is null) return TagEndpoints.NotDeleted("bucket");

        // CreatedAt stays, so it goes back to its place in the stack and takes back the notes it covers.
        bucket.DeletedAt = null;
        var others = await db.Buckets.Where(b => b.BoardId == bucket.BoardId && b.Id != bucketId).ToListAsync(ct);
        var notes = await db.Notes.Where(n => n.BoardId == bucket.BoardId).ToListAsync(ct);
        Layout.AssignBuckets(notes, [.. others, bucket]);

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(BucketDetail.From(bucket));
    }
}
