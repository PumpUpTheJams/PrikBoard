using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class LabelEndpoints
{
    public static RouteGroupBuilder MapLabelEndpoints(this RouteGroupBuilder api)
    {
        var labels = api.MapGroup("/").WithTags("Labels");

        labels.MapPost("/boards/{boardId:guid}/labels", CreateLabel)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createLabel")
            .WithSummary("Add a label")
            .WithDescription("Adds rich text to the board, without a background unless you ask for one, or to a card's own canvas with cardId, with one unless you say otherwise. Give its text as content " +
                             "or markdown, and x and y (snapped to the 20 px grid), or leave them out for a free spot. Give the width and " +
                             "height you measured, if you can; otherwise the server estimates them from the text. " +
                             $"A board holds at most {BoardLabel.MaxPerBoard} labels (409 beyond that).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        labels.MapPatch("/labels/{labelId:guid}", UpdateLabel)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Label)
            .WithName("updateLabel")
            .WithSummary("Change or move a label")
            .WithDescription("Changes its text, position, ink, size or background, puts it on a card's canvas (cardId, with x and y on that canvas), or puts a " +
                             "label on a card back on the board (detach, with x and y on the board). Omitted fields stay unchanged. New " +
                             "markdown keeps the label's own ink and size, but loses underline and any ink or size on single words.")
            .ProducesValidationProblem();

        labels.MapDelete("/labels/{labelId:guid}", DeleteLabel)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Label)
            .WithName("deleteLabel")
            .WithSummary("Delete a label")
            .WithDescription($"Deletes the label. restoreLabel brings it back within {Tombstones.RetentionText}.");

        labels.MapPost("/labels/{labelId:guid}/restore", RestoreLabel)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Label)
            .WithName("restoreLabel")
            .WithSummary("Restore a deleted label")
            .WithDescription($"Brings back a label deleted in the last {Tombstones.RetentionText}, with the same id, place and text. " +
                             "409 when it is not deleted, or when it was on a card that is deleted (restore the card instead).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    internal static async Task<Results<Created<LabelDetail>, NotFound, ValidationProblem, ProblemHttpResult>> CreateLabel(
        Guid boardId, CreateLabelRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        // The board exists: the role check (or the MCP tool's) found it.
        if (Problems.XWithoutY(request.X, request.Y) is { } halfPlace) return halfPlace;
        if (SizeProblem(request.Width, request.Height) is { } halfSize) return halfSize;
        var placed = request.X is not null;
        if (Problems.Drop(request.BucketId, request.Loose, placed) is { } drop) return drop;
        if (Blocks(request.Content, request.Markdown, required: true, out var blocks) is { } invalid) return invalid;

        if (request.CardId is not null && !await db.Cards.AnyAsync(n => n.Id == request.CardId && n.BoardId == boardId, ct))
        {
            return Problems.CardNotOnBoard();
        }
        if (request.CardId is not null && (request.BucketId is not null || request.Loose == true))
        {
            return Problems.Invalid(request.BucketId is null ? "loose" : "bucketId", "Only for a label on the board.");
        }
        if (await db.Labels.CountAsync(l => l.BoardId == boardId, ct) >= BoardLabel.MaxPerBoard)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many labels",
                detail: $"A board holds at most {BoardLabel.MaxPerBoard} labels. Delete some first.");
        }

        var label = new BoardLabel
        {
            BoardId = boardId,
            CardId = request.CardId,
            Ink = request.Ink,
            Size = request.Size,
            Background = request.Background ?? request.CardId is not null,
            AddedBy = await db.Principals.FindAsync([principal.PrincipalId()], ct),
        };
        label.SetWrap(request.WrapWidth);
        label.SetContent(await ShortenLinksAsync(db, boardId, blocks!, ct));
        Measure(label, request.Width, request.Height);

        if (placed) Place(label, request.X!.Value, request.Y!.Value);
        else await FreeSpotAsync(db, label, ct);

        if (request.BucketId is not null || request.Loose == true)
        {
            if (BucketEndpoints.DropIn(label, request.BucketId, await BucketEndpoints.StackAsync(db, boardId, ct)) is { } outside) return outside;
        }
        else
        {
            Layout.Place(label, await BucketEndpoints.StackAsync(db, boardId, ct));
        }

        // On a card: saved under its lock, so the card can't be deleted in between and leave this label live.
        await using var transaction = label.CardId is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (label.CardId is { } cardId && !await CardEndpoints.LockLiveCardAsync(db, cardId, boardId, ct)) return Problems.CardNotOnBoard();
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/labels/{label.Id}", LabelDetail.From(label));
    }

    internal static async Task<Results<Ok<LabelDetail>, NotFound, ValidationProblem>> UpdateLabel(
        Guid labelId, UpdateLabelRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var label = await db.Labels.Include(l => l.AddedBy).FirstOrDefaultAsync(l => l.Id == labelId, ct);
        if (label is null) return TypedResults.NotFound();

        var detach = request.Detach == true;
        if (request.CardId is not null && detach) return Problems.Invalid("detach", "Give either a cardId or detach, not both.");
        if (SizeProblem(request.Width, request.Height) is { } halfSize) return halfSize;
        if (Blocks(request.Content, request.Markdown, required: false, out var blocks) is { } invalid) return invalid;
        if (!string.IsNullOrWhiteSpace(request.AppendText))
        {
            var (appended, tooMuch) = BoardLabel.Check([.. blocks ?? label.Content, .. LabelMarkdown.Parse(request.AppendText)]);
            if (tooMuch is { } bad) return Problems.Invalid("appendText", bad.Message);
            blocks = appended;
        }

        var moving = (request.CardId is { } to && to != label.CardId) || (detach && label.CardId is not null);
        // Onto a card, it finds a free spot by itself without a place (as an image does); onto the board, it needs one.
        if (moving && detach && (request.X is null || request.Y is null))
        {
            return Problems.Invalid(request.X is null ? "x" : "y", "Give x and y on the board when taking a label off its card.");
        }
        if (Problems.XWithoutY(moving ? request.X : 0, moving ? request.Y : 0) is { } halfPlace) return halfPlace;
        var dropped = request.BucketId is not null || request.Loose == true;
        if (Problems.Drop(request.BucketId, request.Loose, request.X is not null && request.Y is not null) is { } drop) return drop;
        if (moving && !detach)
        {
            if (!await db.Cards.AnyAsync(n => n.Id == request.CardId && n.BoardId == label.BoardId, ct))
            {
                return Problems.CardNotOnBoard();
            }
            label.CardId = request.CardId;
        }
        else if (moving)
        {
            label.CardId = null;
        }

        if (blocks is not null) label.SetContent(await ShortenLinksAsync(db, label.BoardId, blocks, ct));
        if (request.Ink is { } ink) label.Ink = ink;
        if (request.Size is { } size) label.Size = size;
        if (request.WrapWidth is { } wrap) label.SetWrap(wrap);
        if (request.Background is { } background) label.Background = background;
        if (request.Width is not null) Measure(label, request.Width, request.Height);
        // Someone who just stopped it wrapping means it.
        else if (request.WrapWidth == 0) label.Estimate();
        else if (blocks is not null || request.Size is not null || request.WrapWidth is not null) label.EstimateAndWrap();

        if (moving && !detach && request.X is null) await FreeSpotAsync(db, label, ct);
        else Place(label, request.X ?? label.X, request.Y ?? label.Y);
        // A label that moved joins the bucket it now sits in (or the one the pointer was in when it was dropped in the web
        // app); new text, ink or size leaves it where it is.
        if (dropped)
        {
            if (label.CardId is not null) return Problems.Invalid(request.BucketId is null ? "loose" : "bucketId", "Only for a label on the board.");
            if (BucketEndpoints.DropIn(label, request.BucketId, await BucketEndpoints.StackAsync(db, label.BoardId, ct)) is { } outside) return outside;
        }
        else if (moving || request.X is not null || request.Y is not null)
        {
            Layout.Place(label, await BucketEndpoints.StackAsync(db, label.BoardId, ct));
        }
        label.UpdatedAt = DateTimeOffset.UtcNow;

        // Onto a card: saved under its lock, so the card can't be deleted in between and leave this label live.
        var onto = moving ? label.CardId : null;
        await using var transaction = onto is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (onto is { } cardId && !await CardEndpoints.LockLiveCardAsync(db, cardId, label.BoardId, ct)) return Problems.CardNotOnBoard();
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Ok(LabelDetail.From(label));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteLabel(
        Guid labelId, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var label = await db.Labels.AsNoTracking().Where(l => l.Id == labelId).Select(l => new { l.BoardId, l.CardId }).FirstOrDefaultAsync(ct);
        var deleted = await db.Labels.Where(l => l.Id == labelId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeletedAt, DateTimeOffset.UtcNow), ct);
        if (deleted == 0) return TypedResults.NotFound();
        if (label?.CardId is { } cardId)
        {
            CardHistory.Record(db, principal, cardId, label.BoardId, new(CardChangeKind.LabelRemoved, Ref: labelId));
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<LabelDetail>, NotFound, ProblemHttpResult>> RestoreLabel(
        Guid labelId, PrikBoardDbContext db, CancellationToken ct)
    {
        var label = await db.Labels.WithDeleted().Include(l => l.AddedBy).FirstOrDefaultAsync(l => l.Id == labelId, ct);
        if (label is null) return TypedResults.NotFound();
        if (label.DeletedAt is null) return Problems.NotDeleted("label");
        if (label.CardId is { } cardId && !await db.Cards.AnyAsync(n => n.Id == cardId, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Card deleted",
                detail: "The label is on a deleted card. Restore the card (restoreCard) to bring back its labels.");
        }

        label.DeletedAt = null;
        Layout.Recheck(label, await BucketEndpoints.StackAsync(db, label.BoardId, ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(LabelDetail.From(label));
    }

    /// <summary>
    /// Checks and cleans up blocks from a clip. Errors are keyed <c>{prefix}content…</c>. Only for content from a clip, whose
    /// links a paste follows itself; requests go through <see cref="Blocks"/>.
    /// </summary>
    internal static ValidationProblem? Build(BoardLabel label, ClippedLabel source, string prefix)
    {
        var (blocks, problem) = BoardLabel.Check(source.Content);
        if (problem is { } bad) return Problems.Invalid(prefix + bad.Field, bad.Message);
        label.SetContent(blocks!);
        label.Ink = source.Ink;
        label.Size = source.Size;
        label.SetWrap(source.WrapWidth);
        label.Background = source.Background ?? source.CardId is not null;
        if (source.Width is { } width && source.Height is { } height) label.SetSize(width, height);
        else label.Estimate();
        Place(label, source.X, source.Y);
        return null;
    }

    /// <summary>
    /// A new card's text as a label at the top of its canvas, not yet added to the database: what createCard's body and agents' and
    /// chats' cards get.
    /// </summary>
    internal static async Task<(BoardLabel? Label, ValidationProblem? Problem)> CardBodyAsync(PrikBoardDbContext db, Card card, string markdown, ClaimsPrincipal principal, CancellationToken ct)
    {
        var (blocks, problem) = BoardLabel.Check(LabelMarkdown.Parse(markdown));
        if (problem is { } bad) return (null, Problems.Invalid("body", bad.Message));
        var label = new BoardLabel
        {
            BoardId = card.BoardId,
            CardId = card.Id,
            X = Layout.Grid,
            Y = Layout.Grid,
            Size = BoardLabel.CardTextSize,
            Background = true,
            AddedById = principal.PrincipalId(),
        };
        label.SetContent(await ShortenLinksAsync(db, card.BoardId, blocks!, ct));
        label.EstimateAndWrap();
        return (label, null);
    }

    /// <summary>The blocks a request gives, as content or markdown, checked; null with none (a problem when <paramref name="required"/>).</summary>
    private static ValidationProblem? Blocks(List<LabelBlock>? content, string? markdown, bool required, out List<LabelBlock>? blocks)
    {
        blocks = null;
        if (content is not null && markdown is not null) return Problems.Invalid("markdown", "Give either content or markdown, not both.");
        if (content is null && markdown is null)
        {
            return required ? Problems.Invalid("content", "A label has text: give content or markdown.") : null;
        }
        var (clean, problem) = BoardLabel.Check(markdown is null ? content : LabelMarkdown.Parse(markdown));
        if (problem is { } bad) return Problems.Invalid(markdown is null ? bad.Field : "markdown", bad.Message);
        blocks = clean;
        return null;
    }

    private static ValidationProblem? SizeProblem(int? width, int? height) =>
        width is null != height is null ? Problems.Invalid(width is null ? "width" : "height", "Give width and height together.") : null;

    private static void Measure(BoardLabel label, int? width, int? height)
    {
        if (width is { } w && height is { } h) label.SetSize(w, h);
        else label.EstimateAndWrap();
    }

    /// <summary>Snaps x and y; on a card, also keeps the label on its canvas, at the last grid line that fits.</summary>
    private static void Place(BoardLabel label, int x, int y) =>
        (label.X, label.Y) = label.CardId is null
            ? (Layout.Snap(x), Layout.Snap(y))
            : (OnCard(x, label.Width), OnCard(y, label.Height));

    private static int OnCard(int value, int size) =>
        Math.Clamp(Layout.Snap(value), 0, Math.Max(0, (Layout.CardCanvasMax - size) / Layout.Grid * Layout.Grid));

    private static async Task FreeSpotAsync(PrikBoardDbContext db, BoardLabel label, CancellationToken ct)
    {
        if (label.CardId is { } cardId)
        {
            var card = await db.Cards.AsNoTracking().FirstAsync(n => n.Id == cardId, ct);
            var attached = await db.Images.AsNoTracking().Where(i => i.CardId == cardId).ToListAsync(ct);
            var labels = await db.Labels.AsNoTracking().Where(l => l.CardId == cardId).ToListAsync(ct);
            List<(int X, int Y, int Width, int Height)> taken =
                [.. Layout.TakenOnCard(card, attached), .. labels.Select(l => (l.X, l.Y, l.Width, l.Height))];
            (label.X, label.Y) = Layout.NextCardSpot(taken, label.Width, label.Height);
            return;
        }
        (label.X, label.Y) = Layout.NextLabelSpot(
            await db.Buckets.Where(b => b.BoardId == label.BoardId).ToListAsync(ct),
            await db.Cards.AsNoTracking().Where(n => n.BoardId == label.BoardId).ToListAsync(ct),
            await db.Images.AsNoTracking().Where(i => i.BoardId == label.BoardId).ToListAsync(ct),
            await db.Labels.AsNoTracking().Where(l => l.BoardId == label.BoardId && l.CardId == null).ToListAsync(ct));
    }

    /// <summary>Links in the older form to cards on <paramref name="boardId"/> become those cards' paths, as in <see cref="CardComponentEndpoints.ShortenLinksAsync"/>.</summary>
    private static async Task<List<LabelBlock>> ShortenLinksAsync(PrikBoardDbContext db, Guid boardId, List<LabelBlock> blocks, CancellationToken ct)
    {
        var ids = new List<Guid>();
        foreach (var run in blocks.SelectMany(b => b.AllRuns()))
        {
            if (run.Link is { } link && CardLinks.TryParse(link, out var target) && !target.ByNumber) ids.Add(target.CardId);
        }
        if (ids.Count == 0) return blocks;

        var paths = await CardComponentEndpoints.CardPathsAsync(db, boardId, ids, ct);
        return MapLinks(blocks, link => CardLinks.TryParse(link, out var target) && !target.ByNumber && paths.TryGetValue(target.CardId, out var path) ? path : link);
    }

    /// <summary>The blocks with each link replaced; a null drops the link and keeps its text.</summary>
    internal static List<LabelBlock> MapLinks(List<LabelBlock> blocks, Func<string, string?> map) =>
        [.. blocks.Select(block => block with
        {
            Runs = MapRunLinks(block.Runs, map),
            Cells = block.Cells?.ConvertAll(cell => MapRunLinks(cell, map)),
        })];

    private static List<LabelRun> MapRunLinks(List<LabelRun> runs, Func<string, string?> map) =>
        [.. runs.Select(run => run.Link is { } link ? run with { Link = map(link) } : run)];
}
