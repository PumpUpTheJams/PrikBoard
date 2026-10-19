using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// The blocks on a note's own canvas. Each write locks the note's row, so people editing different blocks of the same
/// note at once never overwrite each other's changes.
/// </summary>
public static class NoteBlockEndpoints
{
    public static RouteGroupBuilder MapNoteBlockEndpoints(this RouteGroupBuilder api)
    {
        var blocks = api.MapGroup("/notes/{noteId:guid}/blocks").WithTags("Notes");

        blocks.MapPost("/", AddNoteBlock)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("addNoteBlock")
            .WithSummary("Add a block to a note")
            .WithDescription("Adds a text, checklist or links block to the note's content. Without x and y it goes in the first " +
                             $"free spot on the note's canvas. A note has at most {NoteBlock.MaxBlocks} blocks.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        blocks.MapPatch("/{blockId:guid}", UpdateNoteBlock)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("updateNoteBlock")
            .WithSummary("Edit, move or resize a block")
            .WithDescription("Changes any of a block's title, content (text, checklist items or links, whichever fits its kind), " +
                             "position and size. Omitted fields stay unchanged; a list that is given replaces the block's whole list.")
            .ProducesValidationProblem();

        blocks.MapDelete("/{blockId:guid}", DeleteNoteBlock)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Note)
            .WithName("deleteNoteBlock")
            .WithSummary("Remove a block from a note");

        return api;
    }

    internal static async Task<Results<Created<NoteBlockDetail>, NotFound, ValidationProblem, ProblemHttpResult>> AddNoteBlock(
        Guid noteId, NewNoteBlock request, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedNoteAsync(db, noteId, ct) is not { } note) return TypedResults.NotFound();
        if (note.Blocks.Count >= NoteBlock.MaxBlocks)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many blocks",
                detail: $"The note already has {NoteBlock.MaxBlocks} blocks; remove one first.");
        }

        var taken = Layout.TakenOnNote(note, await db.Images.Where(i => i.NoteId == noteId).ToListAsync(ct));
        if (Build(request, taken, "") is not { } block) return InvalidBlock(request, "");
        if (Check(block, "") is { } invalid) return invalid;

        note.Blocks.Add(block);
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/notes/{noteId}/blocks/{block.Id}", NoteBlockDetail.From(block));
    }

    internal static async Task<Results<Ok<NoteBlockDetail>, NotFound, ValidationProblem>> UpdateNoteBlock(
        Guid noteId, Guid blockId, UpdateNoteBlockRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedNoteAsync(db, noteId, ct) is not { } note) return TypedResults.NotFound();
        if (note.Blocks.Find(b => b.Id == blockId) is not { } block) return TypedResults.NotFound();

        if (Misfit(block.Kind, request.Text, request.Items, request.Links, "") is { } misfit) return misfit;
        block.Title = Clean(PatchText.Apply(block.Title, request.Title));
        if (block.Kind == NoteBlockKind.Text) block.Text = PatchText.Apply(block.Text, request.Text);
        if (request.Items is not null) block.Items = Items(request.Items);
        if (request.Links is not null) block.Links = Links(request.Links);
        block.X = request.X ?? block.X;
        block.Y = request.Y ?? block.Y;
        block.Width = request.Width ?? block.Width;
        block.Height = request.Height ?? block.Height;
        Layout.Clamp(block);
        if (Check(block, "") is { } invalid) return invalid;

        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(NoteBlockDetail.From(block));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteNoteBlock(
        Guid noteId, Guid blockId, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedNoteAsync(db, noteId, ct) is not { } note) return TypedResults.NotFound();
        if (note.Blocks.RemoveAll(b => b.Id == blockId) == 0) return TypedResults.NotFound();

        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Builds the blocks of a new note, laying out the ones without a position. Returns a problem (keyed on
    /// "blocks[i].field", after <paramref name="keyPrefix"/>) for content that does not fit a block's kind or an invalid link.
    /// </summary>
    internal static ValidationProblem? BuildAll(Note note, IReadOnlyList<NewNoteBlock> requests, string keyPrefix = "")
    {
        var taken = Layout.TakenOnNote(note, []);
        for (var i = 0; i < requests.Count; i++)
        {
            var prefix = $"{keyPrefix}blocks[{i}].";
            if (Build(requests[i], taken, prefix) is not { } block) return InvalidBlock(requests[i], prefix);
            if (Check(block, prefix) is { } invalid) return invalid;
            note.Blocks.Add(block);
            taken.Add((block.X, block.Y, block.Width, block.Height));
        }
        return null;
    }

    /// <summary>A block from a request, placed in a free spot without X and Y; null when the request does not fit its kind.</summary>
    private static NoteBlock? Build(NewNoteBlock request, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, string prefix)
    {
        if (request.Kind is not { } kind || Misfit(kind, request.Text, request.Items, request.Links, prefix) is not null
            || request.X is null != request.Y is null)
        {
            return null;
        }

        var (width, height) = Layout.DefaultBlockSize(kind);
        var block = new NoteBlock
        {
            Kind = kind,
            Title = Clean(PatchText.Apply(null, request.Title)),
            Text = kind == NoteBlockKind.Text ? PatchText.Apply(null, request.Text) : null,
            Items = Items(request.Items ?? []),
            Links = Links(request.Links ?? []),
            Width = request.Width ?? width,
            Height = request.Height ?? height,
        };
        Layout.Clamp(block);
        (block.X, block.Y) = request.X is { } x && request.Y is { } y ? (x, y) : Layout.NextNoteSpot(taken, block.Width, block.Height);
        Layout.Clamp(block);
        return block;
    }

    /// <summary>Why <see cref="Build"/> returned null.</summary>
    private static ValidationProblem InvalidBlock(NewNoteBlock request, string prefix) =>
        request.Kind is not { } kind ? Problem(prefix + "kind", "Give the kind: Text, Checklist or Links.")
        : Misfit(kind, request.Text, request.Items, request.Links, prefix)
          ?? Problem(prefix + (request.X is null ? "x" : "y"), "Give X and Y together.");

    /// <summary>A problem when content is given that does not fit a block of <paramref name="kind"/>.</summary>
    private static ValidationProblem? Misfit(NoteBlockKind kind, string? text, List<ChecklistItem>? items, List<NoteLink>? links, string prefix) =>
        kind != NoteBlockKind.Text && !string.IsNullOrEmpty(text) ? Problem(prefix + "text", $"A {kind} block has no text; use a Text block.")
        : kind != NoteBlockKind.Checklist && items is { Count: > 0 } ? Problem(prefix + "items", $"A {kind} block has no checklist items; use a Checklist block.")
        : kind != NoteBlockKind.Links && links is { Count: > 0 } ? Problem(prefix + "links", $"A {kind} block has no links; use a Links block.")
        : null;

    private static ValidationProblem? Check(NoteBlock block, string prefix) =>
        block.Links.Find(l => !NoteLinks.IsValid(l.Url)) is { } bad
            ? Problem(prefix + "links", $"\"{bad.Url}\" is neither an absolute http or https URL nor a note's path (/#/boards/{{boardId}}/notes/{{noteId}}).")
            : null;

    private static ValidationProblem Problem(string key, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });

    private static string? Clean(string? title) => title?.Trim();

    private static List<ChecklistEntry> Items(IEnumerable<ChecklistItem> items) =>
        items.Select(c => new ChecklistEntry { Text = c.Text.Trim(), Done = c.Done }).ToList();

    private static List<LinkEntry> Links(IEnumerable<NoteLink> links) =>
        links.Select(l => new LinkEntry { Title = l.Title.Trim(), Url = l.Url.Trim() }).ToList();

    /// <summary>Locks the note's row until the transaction ends, then loads it; null when it does not exist.</summary>
    private static async Task<Note?> LockedNoteAsync(PrikBoardDbContext db, Guid noteId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Notes" WHERE "Id" = {noteId} FOR UPDATE""", ct);
        return await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
    }
}
