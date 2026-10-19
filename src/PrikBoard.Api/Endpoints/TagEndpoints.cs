using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// A board's tags. Notes store the names of their tags, so renaming or deleting a tag here rewrites every note on the
/// board in the same transaction.
/// </summary>
public static class TagEndpoints
{
    public static RouteGroupBuilder MapTagEndpoints(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("/boards/{boardId:guid}/tags").WithTags("Tags");

        tags.MapGet("/", ListBoardTags)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("listBoardTags")
            .WithSummary("List a board's tags")
            .WithDescription("Returns the tags defined on the board by name, with how many notes use each. " +
                             "Prefer these over new names when tagging notes. getBoard includes the same list.");

        tags.MapPost("/", CreateBoardTag)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createBoardTag")
            .WithSummary("Add a tag to a board")
            .WithDescription($"Adds a tag without putting it on a note. Using a new name in a note's tags (createNote, updateNote) " +
                             $"adds it too. A board has at most {BoardTag.MaxPerBoard} tags.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        tags.MapPatch("/{tagId:guid}", RenameBoardTag)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("renameBoardTag")
            .WithSummary("Rename a tag")
            .WithDescription("Renames the tag on the board and on every note that has it.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        tags.MapDelete("/{tagId:guid}", DeleteBoardTag)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("deleteBoardTag")
            .WithSummary("Delete a tag")
            .WithDescription("Deletes the tag from the board and removes it from every note that has it. The notes stay. " +
                             $"restoreBoardTag puts it back on those notes within {Tombstones.RetentionText}.");

        tags.MapPost("/{tagId:guid}/restore", RestoreBoardTag)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("restoreBoardTag")
            .WithSummary("Restore a deleted tag")
            .WithDescription($"Brings back a tag deleted in the last {Tombstones.RetentionText}, with the same id, and puts it back " +
                             "(at the end of their tag list) on the notes it was removed from, except notes that have 10 tags by now. " +
                             "409 when it is not deleted, when the board has a tag with that name again, or when the board is full.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    internal static async Task<Ok<List<BoardTagDetail>>> ListBoardTags(Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var tags = await db.BoardTags.AsNoTracking().Where(t => t.BoardId == boardId).ToListAsync(ct);
        var notes = await db.Notes.AsNoTracking().Where(n => n.BoardId == boardId).ToListAsync(ct);
        return TypedResults.Ok(BoardTagDetail.List(tags, notes));
    }

    internal static async Task<Results<Created<BoardTagDetail>, ValidationProblem, ProblemHttpResult>> CreateBoardTag(
        Guid boardId, CreateTagRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        if (Normalized(request.Name) is not { } name) return InvalidName(request.Name);

        var existing = await db.BoardTags.Where(t => t.BoardId == boardId).Select(t => t.Name).ToListAsync(ct);
        if (existing.Contains(name)) return Exists(name);
        if (existing.Count >= BoardTag.MaxPerBoard) return TooMany();

        var tag = new BoardTag { BoardId = boardId, Name = name };
        db.BoardTags.Add(tag);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/boards/{boardId}/tags/{tag.Id}", new BoardTagDetail(tag.Id, tag.Name, 0));
    }

    internal static async Task<Results<Ok<BoardTagDetail>, NotFound, ValidationProblem, ProblemHttpResult>> RenameBoardTag(
        Guid boardId, Guid tagId, RenameTagRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var tag = await db.BoardTags.FirstOrDefaultAsync(t => t.Id == tagId && t.BoardId == boardId, ct);
        if (tag is null) return TypedResults.NotFound();
        if (Normalized(request.Name) is not { } name) return InvalidName(request.Name);

        var old = tag.Name;
        if (name != old)
        {
            if (await db.BoardTags.AnyAsync(t => t.BoardId == boardId && t.Name == name, ct)) return Exists(name);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            tag.Name = name;
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Notes" SET "Tags" = array_replace("Tags", {old}, {name}) WHERE "BoardId" = {boardId}""", ct);
            await transaction.CommitAsync(ct);
        }

        var count = await db.Notes.CountAsync(n => n.BoardId == boardId && n.Tags.Contains(name), ct);
        return TypedResults.Ok(new BoardTagDetail(tag.Id, tag.Name, count));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteBoardTag(
        Guid boardId, Guid tagId, PrikBoardDbContext db, CancellationToken ct)
    {
        var tag = await db.BoardTags.FirstOrDefaultAsync(t => t.Id == tagId && t.BoardId == boardId, ct);
        if (tag is null) return TypedResults.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Deleted notes count too: they get the tag back if they are restored before the tag is.
        tag.RemovedFromNoteIds = await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Where(n => n.BoardId == boardId && n.Tags.Contains(tag.Name))
            .Select(n => n.Id)
            .ToListAsync(ct);
        tag.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "Notes" SET "Tags" = array_remove("Tags", {tag.Name}) WHERE "BoardId" = {boardId}""", ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<BoardTagDetail>, NotFound, ProblemHttpResult>> RestoreBoardTag(
        Guid boardId, Guid tagId, PrikBoardDbContext db, CancellationToken ct)
    {
        var tag = await db.BoardTags.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .FirstOrDefaultAsync(t => t.Id == tagId && t.BoardId == boardId, ct);
        if (tag is null) return TypedResults.NotFound();
        if (tag.DeletedAt is null) return NotDeleted("tag");

        var existing = await db.BoardTags.Where(t => t.BoardId == boardId).Select(t => t.Name).ToListAsync(ct);
        if (existing.Contains(tag.Name)) return Exists(tag.Name);
        if (existing.Count >= BoardTag.MaxPerBoard) return TooMany();

        var noteIds = tag.RemovedFromNoteIds ?? [];
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        tag.DeletedAt = null;
        tag.RemovedFromNoteIds = null;
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE "Notes" SET "Tags" = array_append("Tags", {tag.Name})
            WHERE "BoardId" = {boardId} AND "Id" = ANY({noteIds}) AND NOT ({tag.Name} = ANY("Tags")) AND cardinality("Tags") < {MaxTagsPerNote}
            """, ct);
        await transaction.CommitAsync(ct);

        var count = await db.Notes.CountAsync(n => n.BoardId == boardId && n.Tags.Contains(tag.Name), ct);
        return TypedResults.Ok(new BoardTagDetail(tag.Id, tag.Name, count));
    }

    /// <summary>The most tags on one note (the MaxLength on tags in createNote and updateNote).</summary>
    private const int MaxTagsPerNote = 10;

    /// <summary>The 409 for restoring something that is not deleted.</summary>
    internal static ProblemHttpResult NotDeleted(string what) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Not deleted", detail: $"The {what} is not deleted, so there is nothing to restore.");

    /// <summary>
    /// Adds the names a note is about to get that the board does not have yet (saved with the note).
    /// Returns a problem when that would take the board over <see cref="BoardTag.MaxPerBoard"/>.
    /// </summary>
    internal static async Task<ValidationProblem?> EnsureBoardTagsAsync(
        PrikBoardDbContext db, Guid boardId, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return null;
        var existing = await db.BoardTags.Where(t => t.BoardId == boardId).Select(t => t.Name).ToListAsync(ct);
        var added = names.Except(existing).ToList();
        if (added.Count == 0) return null;
        if (existing.Count + added.Count > BoardTag.MaxPerBoard)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["tags"] = [$"The board already has {existing.Count} tags; it can have at most {BoardTag.MaxPerBoard}. Use existing tags (listBoardTags) or delete some."],
            });
        }

        db.BoardTags.AddRange(added.Select(name => new BoardTag { BoardId = boardId, Name = name }));
        return null;
    }

    private static string? Normalized(string name) => NoteTags.Normalize(name) is { Length: > 0 and <= NoteTags.MaxLength } tag ? tag : null;

    private static ValidationProblem InvalidName(string name) => TypedResults.ValidationProblem(new Dictionary<string, string[]>
    {
        ["name"] = [$"Tag \"{name.Trim()}\" must be 1 to {NoteTags.MaxLength} characters."],
    });

    private static ProblemHttpResult Exists(string name) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Tag exists", detail: $"A tag called \"{name}\" already exists on this board.");

    private static ProblemHttpResult TooMany() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Too many tags",
        detail: $"A board can have at most {BoardTag.MaxPerBoard} tags; delete some first.");
}
