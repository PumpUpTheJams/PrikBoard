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
            .WithDescription("Returns notes in board order (column by column, top to bottom). " +
                             "Optionally filter by column and/or a case-insensitive text search on title and body.");

        notes.MapPost("/boards/{boardId:guid}/notes", CreateNote)
            .WithName("createNote")
            .WithSummary("Stick a new note on a board")
            .WithDescription("Creates a note in the given column (or the board's first column) at the given position (or the bottom).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        notes.MapGet("/notes/{noteId:guid}", GetNote)
            .WithName("getNote")
            .WithSummary("Get a note");

        notes.MapPatch("/notes/{noteId:guid}", UpdateNote)
            .WithName("updateNote")
            .WithSummary("Edit a note")
            .WithDescription("Changes the title, body and/or colour. Omitted fields stay unchanged. Use moveNote to change column or position.")
            .ProducesValidationProblem();

        notes.MapPost("/notes/{noteId:guid}/move", MoveNote)
            .WithName("moveNote")
            .WithSummary("Move a note")
            .WithDescription("Moves the note to a column on the same board (e.g. from \"Doing\" to \"Done\") " +
                             "or reorders it within its column.")
            .ProducesValidationProblem();

        notes.MapDelete("/notes/{noteId:guid}", DeleteNote)
            .WithName("deleteNote")
            .WithSummary("Delete a note");

        return api;
    }

    private static async Task<Results<Ok<List<NoteDetail>>, NotFound>> ListNotes(
        Guid boardId, Guid? columnId, string? search, PrikBoardDbContext db, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        var query = db.Notes.AsNoTracking().Where(n => n.Column!.BoardId == boardId);
        if (columnId is not null)
        {
            query = query.Where(n => n.ColumnId == columnId);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            query = query.Where(n => EF.Functions.Like(n.Title, pattern, @"\")
                                     || (n.Body != null && EF.Functions.Like(n.Body, pattern, @"\")));
        }

        var notes = await query.OrderBy(n => n.Column!.Position).ThenBy(n => n.Position).ToListAsync(ct);
        return TypedResults.Ok(notes.Select(NoteDetail.From).ToList());
    }

    private static async Task<Results<Created<NoteDetail>, NotFound, ValidationProblem, ProblemHttpResult>> CreateNote(
        Guid boardId, CreateNoteRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var columns = await db.Columns.Where(c => c.BoardId == boardId).OrderBy(c => c.Position).ToListAsync(ct);
        if (columns.Count == 0)
        {
            if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Board has no columns",
                detail: "Create a column before adding notes.");
        }

        var column = request.ColumnId is { } columnId ? columns.Find(c => c.Id == columnId) : columns[0];
        if (column is null) return ColumnNotOnBoard();

        var siblings = await db.Notes.Where(n => n.ColumnId == column.Id).ToListAsync(ct);
        var note = new Note
        {
            ColumnId = column.Id,
            Title = request.Title,
            Body = PatchText.Apply(null, request.Body),
            Color = request.Color ?? NoteColor.Yellow,
        };
        Positioning.Place(siblings, note, request.Position);

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
        var note = await db.Notes.Include(n => n.Column).FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return TypedResults.NotFound();

        var target = await db.Columns.FirstOrDefaultAsync(c => c.Id == request.ColumnId, ct);
        if (target is null || target.BoardId != note.Column!.BoardId) return ColumnNotOnBoard();

        var sourceColumnId = note.ColumnId;
        var targetSiblings = await db.Notes.Where(n => n.ColumnId == target.Id).ToListAsync(ct);
        note.ColumnId = target.Id;
        note.Column = target;
        Positioning.Place(targetSiblings, note, request.Position);

        if (sourceColumnId != target.Id)
        {
            var sourceSiblings = await db.Notes.Where(n => n.ColumnId == sourceColumnId && n.Id != noteId).ToListAsync(ct);
            Positioning.Renumber(sourceSiblings);
        }

        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(NoteDetail.From(note));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteNote(
        Guid noteId, PrikBoardDbContext db, CancellationToken ct)
    {
        var note = await db.Notes.FindAsync([noteId], ct);
        if (note is null) return TypedResults.NotFound();

        db.Notes.Remove(note);
        var siblings = await db.Notes.Where(n => n.ColumnId == note.ColumnId && n.Id != noteId).ToListAsync(ct);
        Positioning.Renumber(siblings);

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static ValidationProblem ColumnNotOnBoard() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["columnId"] = ["The column does not exist on this board."],
        });
}
