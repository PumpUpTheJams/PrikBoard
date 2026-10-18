using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
            .WithName("listNotes")
            .WithSummary("List or search notes on a board")
            .WithDescription("Returns notes bucket by bucket (loose notes last), each group top to bottom and left to right. " +
                             "Optionally filter by bucket and/or a case-insensitive text search on title and body.");

        notes.MapPost("/boards/{boardId:guid}/notes", CreateNote)
            .WithName("createNote")
            .WithSummary("Stick a new note on a board")
            .WithDescription("Creates a note in a free spot inside the given bucket (default: the board's first bucket), " +
                             "or exactly at X and Y.")
            .ProducesValidationProblem();

        notes.MapGet("/notes/{noteId:guid}", GetNote)
            .WithName("getNote")
            .WithSummary("Get a note");

        notes.MapPatch("/notes/{noteId:guid}", UpdateNote)
            .WithName("updateNote")
            .WithSummary("Edit a note")
            .WithDescription("Changes the title, body and/or colour. Omitted fields stay unchanged. Use moveNote to move it.")
            .ProducesValidationProblem();

        notes.MapPost("/notes/{noteId:guid}/move", MoveNote)
            .WithName("moveNote")
            .WithSummary("Move a note")
            .WithDescription("Moves the note into a bucket on the same board (e.g. from \"Doing\" to \"Done\"), " +
                             "or to exact X and Y coordinates on the board.")
            .ProducesValidationProblem();

        notes.MapDelete("/notes/{noteId:guid}", DeleteNote)
            .WithName("deleteNote")
            .WithSummary("Delete a note");

        return api;
    }

    private static async Task<Results<Ok<List<NoteDetail>>, NotFound>> ListNotes(
        Guid boardId, Guid? bucketId, string? search, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var query = db.Notes.AsNoTracking().Where(n => n.BoardId == boardId);
        if (bucketId is not null)
        {
            query = query.Where(n => n.BucketId == bucketId);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            query = query.Where(n => EF.Functions.Like(n.Title, pattern, @"\")
                                     || (n.Body != null && EF.Functions.Like(n.Body, pattern, @"\")));
        }

        var notes = await query.ToListAsync(ct);
        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId).ToListAsync(ct);
        return TypedResults.Ok(Layout.ReadingOrder(notes, buckets).Select(NoteDetail.From).ToList());
    }

    private static async Task<Results<Created<NoteDetail>, NotFound, ValidationProblem>> CreateNote(
        Guid boardId, CreateNoteRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var note = new Note
        {
            BoardId = boardId,
            Title = request.Title,
            Body = PatchText.Apply(null, request.Body),
            Color = request.Color ?? NoteColor.Yellow,
        };
        var buckets = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        var others = await db.Notes.Where(n => n.BoardId == boardId).ToListAsync(ct);
        var bucketId = request.BucketId ?? (request.X is null && request.Y is null ? Layout.BackToFront(buckets).FirstOrDefault()?.Id : null);
        if (Place(note, bucketId, request.X, request.Y, buckets, others) is { } problem) return problem;

        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/notes/{note.Id}", NoteDetail.From(note));
    }

    private static async Task<Results<Ok<NoteDetail>, NotFound>> GetNote(
        Guid noteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(NoteDetail.From(note));
    }

    private static async Task<Results<Ok<NoteDetail>, NotFound>> UpdateNote(
        Guid noteId, UpdateNoteRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.FindAsync([noteId], ct);
        if (note is null) return TypedResults.NotFound();

        note.Title = request.Title ?? note.Title;
        note.Body = PatchText.Apply(note.Body, request.Body);
        note.Color = request.Color ?? note.Color;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(NoteDetail.From(note));
    }

    private static async Task<Results<Ok<NoteDetail>, NotFound, ValidationProblem>> MoveNote(
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
        return TypedResults.Ok(NoteDetail.From(note));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteNote(
        Guid noteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var deleted = await db.Notes.Where(n => n.Id == noteId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    /// <summary>
    /// Positions <paramref name="note"/> exactly at X and Y, or in a free spot inside the bucket, or (with neither)
    /// in a free spot on the board, then sets its bucket from where it landed. Returns a problem for an invalid combination.
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

        if (x is { } exactX && y is { } exactY)
        {
            (note.X, note.Y) = (Layout.Snap(exactX), Layout.Snap(exactY));
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
            (note.X, note.Y) = Layout.FreeSpotIn(bucket, others);
        }
        else
        {
            (note.X, note.Y) = Layout.FreeSpotOutside(buckets, others);
        }

        Layout.AssignBuckets([note], buckets);
        return null;
    }
}
