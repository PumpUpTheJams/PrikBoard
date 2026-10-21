using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class NoteEndpoints
{
    public static RouteGroupBuilder MapNoteEndpoints(this RouteGroupBuilder api)
    {
        var notes = api.MapGroup("/").WithTags("Notes");

        notes.MapGet("/boards/{boardId:guid}/notes", ListNotes)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("listNotes")
            .WithSummary("List or search notes on a board")
            .WithDescription("Returns notes bucket by bucket (loose notes last), each group top to bottom and left to right. " +
                             "Optionally filter by bucket, by tags (repeat tag for several; a note matches when it has any of them), " +
                             "by assignee (a principal id, or a current display name, case-insensitive) " +
                             "and/or a case-insensitive text search on the title and all of the note's content (text, checklist items and links).");

        notes.MapPost("/boards/{boardId:guid}/notes", CreateNote)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createNote")
            .WithSummary("Stick a new note on a board")
            .WithDescription("Creates a note in a free spot inside the given bucket (default: the board's first bucket), " +
                             "or exactly at X and Y, optionally with its first content blocks. " +
                             "Tags the board does not have yet are added to it (see listBoardTags).")
            .ProducesValidationProblem();

        notes.MapGet("/notes/{noteId:guid}", GetNote)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Note)
            .WithName("getNote")
            .WithSummary("Get a note");

        notes.MapGet($"/boards/{{shortId:length({Board.ShortIdLength})}}/notes/{{number:int}}", GetNoteByNumber)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.ShortBoard)
            .WithName("getNoteByNumber")
            .WithSummary("Get a note by its number")
            .WithDescription("Finds a note by its board's short id and its number, the two parts of its path " +
                             "(/#/boards/{shortId}/notes/{number}, path in getNote). Use this to open a link to a note you were given; " +
                             "the other note endpoints take the id it returns.");

        notes.MapPatch("/notes/{noteId:guid}", UpdateNote)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("updateNote")
            .WithSummary("Edit a note")
            .WithDescription("Changes any of title, fill, tags, assignees and the agent-working flag. " +
                             "Omitted fields stay unchanged; a list that is given replaces the whole list. Use moveNote to move it, " +
                             "and addNoteBlock, updateNoteBlock and deleteNoteBlock to change its content.")
            .ProducesValidationProblem();

        notes.MapPost("/notes/{noteId:guid}/working", SetNoteWorking)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("setNoteWorking")
            .WithSummary("Say you are working on a note")
            .WithDescription("With working true, assigns you (the caller) to the note and shows a halo around the post-it; " +
                             "with false, the halo goes and you stay assigned. Agents call this when they start and finish a note.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        notes.MapPost("/notes/{noteId:guid}/move", MoveNote)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("moveNote")
            .WithSummary("Move a note")
            .WithDescription("Moves the note into a bucket on the same board (e.g. from \"Doing\" to \"Done\"), " +
                             "or to exact X and Y coordinates on the board.")
            .ProducesValidationProblem();

        notes.MapDelete("/notes/{noteId:guid}", DeleteNote)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("deleteNote")
            .WithSummary("Delete a note")
            .WithDescription($"Deletes the note and the images attached to it. restoreNote brings them back within {Tombstones.RetentionText}.");

        notes.MapPost("/notes/{noteId:guid}/restore", RestoreNote)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("restoreNote")
            .WithSummary("Restore a deleted note")
            .WithDescription($"Brings back a note deleted in the last {Tombstones.RetentionText}: same id, content, creator and " +
                             "images, at the place it had, or at X and Y when both are given (a note dragged out of the trash). " +
                             "It joins whichever bucket is there now. 409 when the note is not deleted; 404 when it never existed " +
                             "or is gone for good.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        notes.MapGet("/boards/{boardId:guid}/trash", ListDeletedNotes)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("listDeletedNotes")
            .WithSummary("List the notes in the trash")
            .WithDescription($"Returns the notes deleted in the last {Tombstones.RetentionText} (deletedAt set), most recently " +
                             "deleted first, with the images deleted along with them. Optionally only notes whose title or content " +
                             "contains search (case-insensitive). restoreNote brings one back.");

        notes.MapDelete("/boards/{boardId:guid}/trash/{noteId:guid}", PurgeNote)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("purgeNote")
            .WithSummary("Delete a note in the trash for good")
            .WithDescription("Deletes a note that is in the board's trash, with the images and drawings deleted along with it, " +
                             "for good: restoreNote can no longer bring it back. 409 when the note is not deleted; 404 when it " +
                             "is not on this board.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        notes.MapDelete("/boards/{boardId:guid}/trash", EmptyTrash)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("emptyTrash")
            .WithSummary("Empty the board's trash")
            .WithDescription("Deletes every note in the board's trash for good, with the images and drawings deleted along " +
                             "with them. Returns how many notes went.");

        return api;
    }

    internal static async Task<Results<Ok<List<NoteDetail>>, NotFound>> ListNotes(
        Guid boardId, Guid? bucketId, string[]? tag, string? assignee, string? search, PrikBoardDbContext db, CancellationToken ct)
    {
        if (await BoardRefAsync(db, boardId, ct) is not { } board) return TypedResults.NotFound();

        var query = db.Notes.AsNoTracking().Where(n => n.BoardId == boardId);
        if (bucketId is not null)
        {
            query = query.Where(n => n.BucketId == bucketId);
        }

        var notes = await query.ToListAsync(ct);
        var principals = await PrincipalsOnAsync(db, notes, ct);
        // Boards are small, so text, tags and assignees are filtered in memory.
        if (!string.IsNullOrWhiteSpace(search))
        {
            notes = notes.Where(n => ContainsText(n, search.Trim())).ToList();
        }
        if (tag is not null && NoteTags.Normalize(tag) is { Count: > 0 } wanted)
        {
            notes = notes.Where(n => n.Tags.Any(wanted.Contains)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(assignee))
        {
            var name = assignee.Trim();
            Guid? id = Guid.TryParse(name, out var parsed) ? parsed : null;
            notes = notes.Where(n => n.AssigneeIds.Any(a => a == id
                || (principals.TryGetValue(a, out var p) && p.ShownName.Equals(name, StringComparison.OrdinalIgnoreCase)))).ToList();
        }
        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId).ToListAsync(ct);
        return TypedResults.Ok(Layout.ReadingOrder(notes, buckets).Select(n => NoteDetail.From(n, board, principals)).ToList());
    }

    internal static async Task<Results<Created<NoteDetail>, NotFound, ValidationProblem>> CreateNote(
        Guid boardId, CreateNoteRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var note = new Note
        {
            BoardId = boardId,
            Title = request.Title.Trim(),
            Color = request.Color ?? NoteColor.Butter,
            AgentWorking = request.AgentWorking ?? false,
            CreatedById = principal.PrincipalId(),
        };
        if (request.AssigneeIds is not null && await CheckAssigneesAsync(db, boardId, request.AssigneeIds, [], ct) is { } unassignable)
        {
            return unassignable;
        }
        if (ApplyLists(note, request.Tags, request.AssigneeIds) is { } invalid) return invalid;
        if (request.Blocks is not null && NoteBlockEndpoints.BuildAll(note, request.Blocks) is { } invalidBlock) return invalidBlock;
        await NoteBlockEndpoints.ShortenLinksAsync(db, boardId, note.Blocks, ct);
        if (await TagEndpoints.EnsureBoardTagsAsync(db, boardId, note.Tags, ct) is { } tooManyTags) return tooManyTags;

        var buckets = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        var others = await db.Notes.Where(n => n.BoardId == boardId).ToListAsync(ct);
        var bucketId = request.BucketId ?? (request.X is null && request.Y is null ? Layout.BackToFront(buckets).FirstOrDefault()?.Id : null);
        if (Place(note, bucketId, request.X, request.Y, buckets, others) is { } problem) return problem;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        note.Number = await TakeNumberAsync(db, boardId, ct);
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/notes/{note.Id}", await DetailAsync(db, note, ct));
    }

    /// <summary>
    /// The board's next note number, counted up in the same statement. That locks the board's row until the transaction
    /// ends, so notes created at the same time get different numbers, and a note that fails to save gives its number back.
    /// </summary>
    private static async Task<int> TakeNumberAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        (await db.Database.SqlQuery<int>($"""
            UPDATE "Boards" SET "NextNoteNumber" = "NextNoteNumber" + 1 WHERE "Id" = {boardId} RETURNING "NextNoteNumber" - 1 AS "Value"
            """).ToListAsync(ct)).Single();

    internal static async Task<Results<Ok<NoteDetail>, NotFound>> GetNote(
        Guid noteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(await DetailAsync(db, note, ct));
    }

    internal static async Task<Results<Ok<NoteDetail>, NotFound>> GetNoteByNumber(
        string shortId, int number, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Board!.ShortId == shortId && n.Number == number, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(await DetailAsync(db, note, ct));
    }

    internal static async Task<Results<Ok<NoteDetail>, NotFound, ValidationProblem>> UpdateNote(
        Guid noteId, UpdateNoteRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.FindAsync([noteId], ct);
        if (note is null) return TypedResults.NotFound();

        if (request.AssigneeIds is not null
            && await CheckAssigneesAsync(db, note.BoardId, request.AssigneeIds, note.AssigneeIds, ct) is { } unassignable)
        {
            return unassignable;
        }
        if (ApplyLists(note, request.Tags, request.AssigneeIds) is { } invalid) return invalid;
        if (request.Tags is not null && await TagEndpoints.EnsureBoardTagsAsync(db, note.BoardId, note.Tags, ct) is { } tooManyTags)
        {
            return tooManyTags;
        }
        note.Title = request.Title?.Trim() ?? note.Title;
        note.Color = request.Color ?? note.Color;
        note.AgentWorking = request.AgentWorking ?? note.AgentWorking;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, note, ct));
    }

    internal static async Task<Results<Ok<NoteDetail>, NotFound, ProblemHttpResult>> SetNoteWorking(
        Guid noteId, SetWorkingRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.FindAsync([noteId], ct);
        if (note is null) return TypedResults.NotFound();

        // Whoever passed the Editor check reaches the board, so they can be assigned.
        var me = principal.PrincipalId();
        if (request.Working && !note.AssigneeIds.Contains(me))
        {
            if (note.AssigneeIds.Count >= MaxAssignees)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many assignees",
                    detail: $"The note already has {MaxAssignees} assignees; remove one first.");
            }
            note.AssigneeIds = [.. note.AssigneeIds, me];
        }
        note.AgentWorking = request.Working;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, note, ct));
    }

    internal static async Task<Results<Ok<NoteDetail>, NotFound, ValidationProblem>> MoveNote(
        Guid noteId, MoveNoteRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.FindAsync([noteId], ct);
        if (note is null) return TypedResults.NotFound();

        if (request is { BucketId: null, X: null, Y: null })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["bucketId"] = ["Give a bucketId, or X and Y."],
            });
        }

        var buckets = await db.Buckets.Where(b => b.BoardId == note.BoardId).ToListAsync(ct);
        var others = await db.Notes.Where(n => n.BoardId == note.BoardId && n.Id != noteId).ToListAsync(ct);
        if (Place(note, request.BucketId, request.X, request.Y, buckets, others) is { } problem) return problem;

        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, note, ct));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteNote(
        Guid noteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Notes.Where(n => n.Id == noteId).ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, now), ct);
        if (deleted == 0) return TypedResults.NotFound();
        // Its images and drawings share the note's stamp, so they come back with it (and ones deleted earlier don't).
        await db.Images.Where(i => i.NoteId == noteId).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, now), ct);
        await db.Drawings.Where(d => d.NoteId == noteId).ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, now), ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Whether the title or any of the note's content contains <paramref name="text"/>, ignoring case.</summary>
    internal static bool ContainsText(Note note, string text) =>
        note.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
        || note.Blocks.Any(b => b.SearchText().Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));

    internal static async Task<Results<Ok<TrashContents>, NotFound>> ListDeletedNotes(
        Guid boardId, string? search, PrikBoardDbContext db, CancellationToken ct)
    {
        if (await BoardRefAsync(db, boardId, ct) is not { } board) return TypedResults.NotFound();

        string[] deleted = [PrikBoardDbContext.SoftDelete];
        var notes = await db.Notes.AsNoTracking().IgnoreQueryFilters(deleted)
            .Where(n => n.BoardId == boardId && n.DeletedAt != null)
            .ToListAsync(ct);
        // Boards are small, so the search runs in memory, like listNotes.
        if (!string.IsNullOrWhiteSpace(search)) notes = notes.Where(n => ContainsText(n, search.Trim())).ToList();
        notes = notes.OrderByDescending(n => n.DeletedAt).ThenByDescending(n => n.Id).ToList();

        // The images that come back with them: deleted at the same moment.
        var stamps = notes.ToDictionary(n => n.Id, n => n.DeletedAt);
        var ids = stamps.Keys.ToList();
        var images = (await db.Images.AsNoTracking().IgnoreQueryFilters(deleted).Include(i => i.AddedBy).Include(i => i.File)
                .Where(i => i.NoteId != null && ids.Contains(i.NoteId.Value))
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .ToListAsync(ct))
            .Where(i => i.DeletedAt == stamps[i.NoteId!.Value])
            .ToList();
        var principals = await PrincipalsOnAsync(db, notes, ct);
        return TypedResults.Ok(new TrashContents(
            notes.Select(n => NoteDetail.From(n, board, principals)).ToList(),
            images.Select(ImageDetail.From).ToList()));
    }

    internal static async Task<Results<Ok<NoteDetail>, NotFound, ValidationProblem, ProblemHttpResult>> RestoreNote(
        Guid noteId, RestoreNoteRequest? request, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return TypedResults.NotFound();
        if (note.DeletedAt is not { } stamp) return TagEndpoints.NotDeleted("note");
        if (request?.X is null != request?.Y is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [request?.X is null ? "x" : "y"] = ["Give X and Y together."],
            });
        }

        // Tags deleted meanwhile were taken off the note as well, so this only fills a gap.
        if (await TagEndpoints.EnsureBoardTagsAsync(db, note.BoardId, note.Tags, ct) is { } tooManyTags) return tooManyTags;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        note.DeletedAt = null;
        await db.Images.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Where(i => i.NoteId == noteId && i.DeletedAt == stamp)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, (DateTimeOffset?)null), ct);
        await db.Drawings.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Where(d => d.NoteId == noteId && d.DeletedAt == stamp)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, (DateTimeOffset?)null), ct);
        var stacked = Layout.BackToFront(await db.Buckets.Where(b => b.BoardId == note.BoardId).ToListAsync(ct));
        if (request is { X: { } x, Y: { } y })
        {
            // Dropped somewhere else (out of the trash): like a move, so it comes out on top, in whatever bucket it lands in.
            (note.X, note.Y) = (Layout.Snap(x), Layout.Snap(y));
            note.UpdatedAt = DateTimeOffset.UtcNow;
            Layout.Place(note, stacked);
        }
        else
        {
            // UpdatedAt stays, so the post-it goes back to its place in the stacking order too.
            // Back in its bucket if that is still around it, or wherever it now lands.
            Layout.Recheck(note, stacked);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, note, ct));
    }

    internal static async Task<Results<NoContent, NotFound, ProblemHttpResult>> PurgeNote(
        Guid boardId, Guid noteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Where(n => n.Id == noteId && n.BoardId == boardId)
            .Select(n => new { n.DeletedAt })
            .FirstOrDefaultAsync(ct);
        if (note is null) return TypedResults.NotFound();
        if (note.DeletedAt is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Not deleted", detail: "The note is not in the trash; delete it first.");
        }
        // Its images and drawings go with it through the cascades; their files at the next sweep.
        await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Where(n => n.Id == noteId && n.DeletedAt != null)
            .ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    internal static async Task<Ok<EmptyTrashResult>> EmptyTrash(Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var purged = await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Where(n => n.BoardId == boardId && n.DeletedAt != null)
            .ExecuteDeleteAsync(ct);
        return TypedResults.Ok(new EmptyTrashResult(purged));
    }

    /// <summary>
    /// The creators and assignees of <paramref name="notes"/>, loaded in one query, so their current names can be shown.
    /// </summary>
    internal static async Task<Dictionary<Guid, Principal>> PrincipalsOnAsync(
        PrikBoardDbContext db, IEnumerable<Note> notes, CancellationToken ct)
    {
        var ids = notes.SelectMany(n => n.CreatedById is { } by ? [by, .. n.AssigneeIds] : n.AssigneeIds).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>What a note's readable id and path need from its board; null when the board does not exist.</summary>
    internal static Task<BoardRef?> BoardRefAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        db.Boards.AsNoTracking().Where(b => b.Id == boardId).Select(b => new BoardRef(b.ShortId, b.Key)).FirstOrDefaultAsync(ct);

    private static async Task<NoteDetail> DetailAsync(PrikBoardDbContext db, Note note, CancellationToken ct) =>
        NoteDetail.From(note, (await BoardRefAsync(db, note.BoardId, ct))!, await PrincipalsOnAsync(db, [note], ct));

    /// <summary>
    /// Positions <paramref name="note"/> exactly at X and Y, or in a free spot inside the bucket (and in it), or (with
    /// neither) in a free spot on the board, then sets its bucket from where it landed. Other notes keep theirs. Returns a
    /// problem for an invalid combination.
    /// </summary>
    private static ValidationProblem? Place(
        Note note, Guid? bucketId, int? x, int? y, List<Bucket> buckets, List<Note> others)
    {
        if (x is null != y is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [x is null ? "x" : "y"] = ["Give X and Y together."],
            });
        }
        if (bucketId is not null && x is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["bucketId"] = ["Give either a bucketId or X and Y, not both."],
            });
        }

        var stacked = Layout.BackToFront(buckets);
        if (x is { } exactX && y is { } exactY)
        {
            (note.X, note.Y) = (Layout.Snap(exactX), Layout.Snap(exactY));
            Layout.Place(note, stacked);
        }
        else if (bucketId is { } id)
        {
            var bucket = buckets.Find(b => b.Id == id);
            if (bucket is null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["bucketId"] = ["The bucket does not exist on this board."],
                });
            }
            (note.X, note.Y) = Layout.MakeRoomIn(bucket, others, buckets);
            // Asked for by name, so it joins even a collapsed bucket (which other notes don't).
            note.BucketId = bucket.Id;
            // A bucket that grew to make room can now cover loose notes too.
            Layout.Adopt(bucket, stacked, others, [], looseOnly: true);
        }
        else
        {
            (note.X, note.Y) = Layout.FreeSpotOutside(buckets, others);
            Layout.Place(note, stacked);
        }
        return null;
    }

    /// <summary>The most people and agents on one note (also the MaxLength on assigneeIds).</summary>
    internal const int MaxAssignees = 10;

    /// <summary>
    /// Checks that everyone being added to a note (<paramref name="ids"/> not in <paramref name="already"/>) can be
    /// assigned on the board. People already on the note may stay after they lose access, so a client can always send the
    /// full list back.
    /// </summary>
    internal static async Task<ValidationProblem?> CheckAssigneesAsync(
        PrikBoardDbContext db, Guid boardId, IEnumerable<Guid> ids, IEnumerable<Guid> already, CancellationToken ct)
    {
        var added = ids.Except(already).ToList();
        if (added.Count == 0) return null;

        var assignable = await BoardAccess.AssignableIds(db, boardId).Where(id => added.Contains(id)).ToListAsync(ct);
        var missing = added.Except(assignable).ToList();
        if (missing.Count == 0) return null;
        return TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["assigneeIds"] = [$"{string.Join(", ", missing)} cannot be assigned: no access to this board. See people in getBoard."],
        });
    }

    /// <summary>Replaces each list that is given (null keeps it), normalizing tags. Returns a problem for an invalid value.</summary>
    private static ValidationProblem? ApplyLists(Note note, List<string>? tags, List<Guid>? assigneeIds)
    {
        if (tags is not null)
        {
            var normalized = NoteTags.Normalize(tags);
            if (normalized.Find(t => t.Length > NoteTags.MaxLength) is { } tooLong)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["tags"] = [$"Tag \"{tooLong}\" is longer than {NoteTags.MaxLength} characters."],
                });
            }
            note.Tags = normalized;
        }
        if (assigneeIds is not null)
        {
            note.AssigneeIds = assigneeIds.Distinct().ToList();
        }
        return null;
    }
}
