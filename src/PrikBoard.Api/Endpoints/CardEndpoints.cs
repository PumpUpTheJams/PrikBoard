using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class CardEndpoints
{
    public static RouteGroupBuilder MapCardEndpoints(this RouteGroupBuilder api)
    {
        var cards = api.MapGroup("/").WithTags("Cards");

        cards.MapGet("/boards/{boardId:guid}/cards", ListCards)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("listCards")
            .WithSummary("List or search cards on a board")
            .WithDescription("Returns cards bucket by bucket (loose cards last), each group top to bottom and left to right. " +
                             "Optionally filter by bucket, by tags (repeat tag for several; a card matches when it has any of them), " +
                             "by assignee (a principal id, or a current display name, case-insensitive) " +
                             "and/or a case-insensitive text search on the title and all of the card's content (text, checklist items and links).");

        cards.MapPost("/boards/{boardId:guid}/cards", CreateCard)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createCard")
            .WithSummary("Create a card")
            .WithDescription("Creates a card in a free spot inside bucketId (default: the board's first bucket; a full bucket grows " +
                             "downwards), or exactly at x and y. With all three, the card goes at x and y and joins that bucket, which " +
                             "must contain its center (400 otherwise). Optionally with its first components. " +
                             "Tags the board does not have yet are added to it (see listBoardTags).")
            .ProducesValidationProblem();

        cards.MapPost("/boards/{boardId:guid}/cards/batch", CreateCards)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("createCards")
            .WithSummary("Create several cards")
            .WithDescription($"Creates up to {MaxBatch} cards at once, in the order given, like createCard does one: each in a free " +
                             "spot of its bucket (default: the board's first bucket) or at its X and Y. All of them are created, or none " +
                             "(a 400 keyed on cards[i]).")
            .ProducesValidationProblem();

        cards.MapGet("/cards/{cardId:guid}", GetCard)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Card)
            .WithName("getCard")
            .WithSummary("Get a card")
            .WithDescription("Returns the card with its components, tags, assignees, readable id and path.");

        cards.MapGet($"/boards/{{shortId:length({Board.ShortIdLength})}}/cards/{{number:int}}", GetCardByNumber)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.ShortBoard)
            .WithName("getCardByNumber")
            .WithSummary("Get a card by its number")
            .WithDescription("Finds a card by its board's short id and its number, the two parts of its path " +
                             "(/#/boards/{shortId}/cards/{number}, path in getCard). Use this to open a link to a card you were given; " +
                             "the other card endpoints take the id it returns.");

        cards.MapPatch("/cards/{cardId:guid}", UpdateCard)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("updateCard")
            .WithSummary("Edit a card")
            .WithDescription("Changes any of title, color (the card's fill), tags, assignees and the agent-working flag. " +
                             "Omitted fields stay unchanged; a list that is given replaces the whole list. New assignees must be able " +
                             "to reach the board (people in getBoard); ones already on the card may stay. Use moveCard to move it, " +
                             "and addCardComponent, updateCardComponent and deleteCardComponent to change its content.")
            .ProducesValidationProblem();

        cards.MapPost("/cards/{cardId:guid}/working", SetCardWorking)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("setCardWorking")
            .WithSummary("Start or stop working on a card")
            .WithDescription("With working true, assigns you (the caller) to the card and shows a halo around it; " +
                             "with false, the halo goes and you stay assigned. Agents call this when they start and finish a card. " +
                             "409 when the card already has the most assignees it can have.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        cards.MapPost("/cards/{cardId:guid}/move", MoveCard)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("moveCard")
            .WithSummary("Move a card")
            .WithDescription("Moves the card into a free spot in a bucket on the same board (e.g. from \"Doing\" to \"Done\"; " +
                             "a full bucket grows downwards), or to exact x and y, where it joins the topmost bucket containing its " +
                             "center. With all three, it goes at x and y and joins that bucket, which must contain its center (400 otherwise).")
            .ProducesValidationProblem();

        cards.MapDelete("/cards/{cardId:guid}", DeleteCard)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("deleteCard")
            .WithSummary("Delete a card")
            .WithDescription($"Deletes the card with its images and drawings. restoreCard brings them back within {Tombstones.RetentionText}.");

        cards.MapPost("/cards/{cardId:guid}/restore", RestoreCard)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("restoreCard")
            .WithSummary("Restore a deleted card")
            .WithDescription($"Brings back a card deleted in the last {Tombstones.RetentionText}: same id, content, creator, " +
                             "images and drawings, at the place it had, or at x and y when both are given (a card dragged out of the trash). " +
                             "It joins whichever bucket is there now. 409 when the card is not deleted; 404 when it never existed " +
                             "or is gone for good.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        cards.MapGet("/cards/{cardId:guid}/history", ListCardHistory)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Card)
            .WithName("listCardHistory")
            .WithSummary("List a card's history")
            .WithDescription("Returns who changed what on the card and when, newest first: title, color, tags, assignees, the " +
                             "agent-working flag, moves between buckets, components, images and drawings added, edited or removed, " +
                             "and deleting and restoring it. Also for cards in the trash. At most limit changes (default 100, at most 500).");

        cards.MapGet("/boards/{boardId:guid}/trash", ListDeletedCards)
            .RequireBoardRole(BoardRole.Viewer)
            .WithName("listDeletedCards")
            .WithSummary("List the cards in the trash")
            .WithDescription($"Returns the cards deleted in the last {Tombstones.RetentionText} (deletedAt set), most recently " +
                             "deleted first, with the images deleted along with them. Optionally only cards whose title or content " +
                             "contains search (case-insensitive). restoreCard brings one back.");

        cards.MapDelete("/boards/{boardId:guid}/trash/{cardId:guid}", PurgeCard)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("purgeCard")
            .WithSummary("Delete a card in the trash for good")
            .WithDescription("Deletes a card that is in the board's trash, with the images and drawings deleted along with it, " +
                             "for good: restoreCard can no longer bring it back. 409 when the card is not deleted; 404 when it " +
                             "is not on this board.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        cards.MapDelete("/boards/{boardId:guid}/trash", EmptyTrash)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("emptyTrash")
            .WithSummary("Empty the board's trash")
            .WithDescription("Deletes every card in the board's trash for good, with the images and drawings deleted along " +
                             "with them. Returns how many cards went.");

        return api;
    }

    internal static async Task<Results<Ok<List<CardDetail>>, NotFound>> ListCards(
        Guid boardId, Guid? bucketId, string[]? tag, string? assignee, string? search, PrikBoardDbContext db, CancellationToken ct)
    {
        if (await BoardRefAsync(db, boardId, ct) is not { } board) return TypedResults.NotFound();

        var query = db.Cards.AsNoTracking().Where(n => n.BoardId == boardId);
        if (bucketId is not null)
        {
            query = query.Where(n => n.BucketId == bucketId);
        }

        var cards = await query.ToListAsync(ct);
        var principals = await PrincipalsOnAsync(db, cards, ct);
        // Boards are small, so text, tags and assignees are filtered in memory.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            cards = cards.Where(n => ContainsText(n, text)).ToList();
        }
        if (tag is not null && CardTags.Normalize(tag) is { Count: > 0 } wanted)
        {
            cards = cards.Where(n => n.Tags.Any(wanted.Contains)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(assignee))
        {
            var name = assignee.Trim();
            Guid? id = Guid.TryParse(name, out var parsed) ? parsed : null;
            cards = cards.Where(n => n.AssigneeIds.Any(a => a == id
                || (principals.TryGetValue(a, out var p) && p.ShownName.Equals(name, StringComparison.OrdinalIgnoreCase)))).ToList();
        }
        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId).ToListAsync(ct);
        return TypedResults.Ok(Layout.ReadingOrder(cards, buckets).Select(n => CardDetail.From(n, board, principals)).ToList());
    }

    internal static Task<Results<Created<CardDetail>, NotFound, ValidationProblem>> CreateCard(
        Guid boardId, CreateCardRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct) =>
        CreateCardAsync(boardId, request, principal, db, assigneesChecked: false, ct);

    /// <summary>
    /// createCard for callers that already checked the assignees can be assigned (MCP tools, which resolve them by name
    /// against the same list), which saves a query.
    /// </summary>
    internal static Task<Results<Created<CardDetail>, NotFound, ValidationProblem>> CreateCardAsync(
        Guid boardId, CreateCardRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, bool assigneesChecked, CancellationToken ct) =>
        AddCardsAsync(boardId, [request], principal, db, assigneesChecked, batch: false,
            cards => TypedResults.Created($"/api/cards/{cards[0].Id}", cards[0]), ct);

    /// <summary>The most cards createCards takes at once.</summary>
    internal const int MaxBatch = 50;

    internal static Task<Results<Ok<List<CardDetail>>, NotFound, ValidationProblem>> CreateCards(
        Guid boardId, CreateCardsRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct) =>
        CreateCardsAsync(boardId, request, principal, db, assigneesChecked: false, ct);

    /// <summary>createCards; see <see cref="CreateCardAsync"/> for <paramref name="assigneesChecked"/>.</summary>
    internal static async Task<Results<Ok<List<CardDetail>>, NotFound, ValidationProblem>> CreateCardsAsync(
        Guid boardId, CreateCardsRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, bool assigneesChecked, CancellationToken ct)
    {
        if (request.Cards.Count == 0) return Problems.Invalid("cards", "Give at least one card.");
        return await AddCardsAsync(boardId, request.Cards, principal, db, assigneesChecked, batch: true, cards => TypedResults.Ok(cards), ct);
    }

    /// <summary>
    /// Creates cards in one transaction, each placed like createCard places one, with room for the ones before it, and
    /// answers with <paramref name="created"/> of them. With <paramref name="batch"/>, problems are keyed on cards[i].
    /// </summary>
    private static async Task<Results<TCreated, NotFound, ValidationProblem>> AddCardsAsync<TCreated>(
        Guid boardId, IReadOnlyList<CreateCardRequest> requests, ClaimsPrincipal principal, PrikBoardDbContext db,
        bool assigneesChecked, bool batch, Func<List<CardDetail>, TCreated> created, CancellationToken ct)
        where TCreated : IResult
    {
        if (await BoardRefAsync(db, boardId, ct) is not { } board) return TypedResults.NotFound();
        ValidationProblem Keyed(ValidationProblem problem, int i) => batch ? Prefixed(problem, $"cards[{i}].") : problem;

        var cards = new List<Card>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var card = new Card
            {
                BoardId = boardId,
                Title = request.Title.Trim(),
                Color = request.Color ?? CardColor.Butter,
                AgentWorking = request.AgentWorking ?? false,
                CreatedById = principal.PrincipalId(),
            };
            if (!assigneesChecked && request.AssigneeIds is not null
                && await CheckAssigneesAsync(db, boardId, request.AssigneeIds, [], ct) is { } unassignable)
            {
                return Keyed(unassignable, i);
            }
            if (ApplyLists(card, request.Tags, request.AssigneeIds) is { } invalid) return Keyed(invalid, i);
            if (request.Components is not null && CardComponentEndpoints.BuildAll(card, request.Components) is { } invalidComponent)
            {
                return Keyed(invalidComponent, i);
            }
            await CardComponentEndpoints.ShortenLinksAsync(db, boardId, card.Components, ct);
            cards.Add(card);
        }
        var tags = cards.SelectMany(c => c.Tags).Distinct().ToList();
        if (await TagEndpoints.EnsureBoardTagsAsync(db, boardId, tags, ct) is { } tooManyTags) return tooManyTags;

        var buckets = await db.Buckets.Where(b => b.BoardId == boardId).ToListAsync(ct);
        var others = await PlacedCardsAsync(db, boardId, null, ct);
        var placed = others.ToDictionary(n => n.Id, n => n.BucketId);
        var first = Layout.InListOrder(buckets).FirstOrDefault()?.Id;
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var bucketId = request.BucketId ?? (request.X is null && request.Y is null ? first : null);
            if (Place(cards[i], bucketId, request.X, request.Y, buckets, others) is { } problem) return Keyed(problem, i);
            // The next card makes room for this one.
            others.Add(cards[i]);
        }
        TrackMoved(db, others, placed);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var number = await TakeNumberAsync(db, boardId, ct, cards.Count);
        foreach (var card in cards) card.Number = number++;
        db.Cards.AddRange(cards);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var principals = await PrincipalsOnAsync(db, cards, ct);
        return created(cards.Select(card => CardDetail.From(card, board, principals)).ToList());
    }

    /// <summary>The same problem with its keys under <paramref name="prefix"/> (cards[2].tags).</summary>
    private static ValidationProblem Prefixed(ValidationProblem problem, string prefix) =>
        TypedResults.ValidationProblem(problem.ProblemDetails.Errors.ToDictionary(e => prefix + e.Key, e => e.Value));

    /// <summary>
    /// The board's other cards, with only what placing a card needs (place and bucket), untracked: a board's cards with
    /// all their content would be loaded and compared on save for nothing. <see cref="TrackMoved"/> saves the ones that
    /// changed bucket.
    /// </summary>
    private static Task<List<Card>> PlacedCardsAsync(PrikBoardDbContext db, Guid boardId, Guid? except, CancellationToken ct) =>
        db.Cards.AsNoTracking()
            .Where(n => n.BoardId == boardId && n.Id != except)
            .Select(n => new Card { Id = n.Id, BoardId = n.BoardId, BucketId = n.BucketId, X = n.X, Y = n.Y, Title = "" })
            .ToListAsync(ct);

    /// <summary>
    /// Saves the bucket of cards from <see cref="PlacedCardsAsync"/> that joined another one (<see cref="Layout.Adopt"/>):
    /// tracked as they were, so only their bucket is written, and their history records the move.
    /// </summary>
    private static void TrackMoved(PrikBoardDbContext db, IEnumerable<Card> cards, Dictionary<Guid, Guid?> was)
    {
        foreach (var card in cards)
        {
            if (!was.TryGetValue(card.Id, out var bucketId) || bucketId == card.BucketId) continue;
            var now = card.BucketId;
            card.BucketId = bucketId;
            db.Cards.Attach(card);
            card.BucketId = now;
        }
    }

    /// <summary>
    /// The board's next card number (the first of <paramref name="count"/> in a row), counted up in the same statement.
    /// That locks the board's row until the transaction ends, so cards created at the same time get different numbers, and
    /// cards that fail to save give their numbers back.
    /// </summary>
    internal static async Task<int> TakeNumberAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct, int count = 1) =>
        (await db.Database.SqlQuery<int>($"""
            UPDATE "Boards" SET "NextCardNumber" = "NextCardNumber" + {count} WHERE "Id" = {boardId} RETURNING "NextCardNumber" - {count} AS "Value"
            """).ToListAsync(ct)).Single();

    internal static async Task<Results<Ok<CardDetail>, NotFound>> GetCard(
        Guid cardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var found = await WithBoardRef(db.Cards.Where(n => n.Id == cardId)).FirstOrDefaultAsync(ct);
        return found is null ? TypedResults.NotFound() : TypedResults.Ok(await DetailAsync(db, found.Card, found.Board, ct));
    }

    internal static async Task<Results<Ok<CardDetail>, NotFound>> GetCardByNumber(
        string shortId, int number, PrikBoardDbContext db, CancellationToken ct)
    {
        var found = await WithBoardRef(db.Cards.Where(n => n.Board!.ShortId == shortId && n.Number == number)).FirstOrDefaultAsync(ct);
        return found is null ? TypedResults.NotFound() : TypedResults.Ok(await DetailAsync(db, found.Card, found.Board, ct));
    }

    internal static async Task<Results<Ok<CardDetail>, NotFound, ValidationProblem>> UpdateCard(
        Guid cardId, UpdateCardRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var card = await db.Cards.FindAsync([cardId], ct);
        if (card is null) return TypedResults.NotFound();

        if (request.AssigneeIds is not null
            && await CheckAssigneesAsync(db, card.BoardId, request.AssigneeIds, card.AssigneeIds, ct) is { } unassignable)
        {
            return unassignable;
        }
        if (ApplyLists(card, request.Tags, request.AssigneeIds) is { } invalid) return invalid;
        if (request.Tags is not null && await TagEndpoints.EnsureBoardTagsAsync(db, card.BoardId, card.Tags, ct) is { } tooManyTags)
        {
            return tooManyTags;
        }
        card.Title = request.Title?.Trim() ?? card.Title;
        card.Color = request.Color ?? card.Color;
        card.AgentWorking = request.AgentWorking ?? card.AgentWorking;
        card.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, card, ct));
    }

    internal static async Task<Results<Ok<CardDetail>, NotFound, ProblemHttpResult>> SetCardWorking(
        Guid cardId, SetWorkingRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var card = await db.Cards.FindAsync([cardId], ct);
        if (card is null) return TypedResults.NotFound();

        // Whoever passed the Editor check reaches the board, so they can be assigned.
        var me = principal.PrincipalId();
        if (request.Working && !card.AssigneeIds.Contains(me))
        {
            if (card.AssigneeIds.Count >= MaxAssignees)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many assignees",
                    detail: $"The card already has {MaxAssignees} assignees; remove one first.");
            }
            card.AssigneeIds = [.. card.AssigneeIds, me];
        }
        card.AgentWorking = request.Working;
        card.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, card, ct));
    }

    internal static async Task<Results<Ok<CardDetail>, NotFound, ValidationProblem>> MoveCard(
        Guid cardId, MoveCardRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        if (request is { BucketId: null, X: null, Y: null }) return Problems.Invalid("bucketId", "Give a bucketId, or X and Y.");
        var card = await db.Cards.FindAsync([cardId], ct);
        if (card is null) return TypedResults.NotFound();

        var buckets = await db.Buckets.Where(b => b.BoardId == card.BoardId).ToListAsync(ct);
        var others = await PlacedCardsAsync(db, card.BoardId, cardId, ct);
        var placed = others.ToDictionary(n => n.Id, n => n.BucketId);
        if (Place(card, request.BucketId, request.X, request.Y, buckets, others) is { } problem) return problem;
        TrackMoved(db, others, placed);

        card.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, card, ct));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteCard(
        Guid cardId, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var boardId = await db.Cards.Where(n => n.Id == cardId).Select(n => (Guid?)n.BoardId).FirstOrDefaultAsync(ct);
        if (await Tombstones.DeleteCardsAsync(db, [cardId], now, ct) == 0 || boardId is null) return TypedResults.NotFound();
        CardHistory.Record(db, principal, cardId, boardId.Value, new(CardChangeKind.Deleted));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>How many changes listCardHistory returns without a limit, and at most.</summary>
    private const int DefaultHistoryLimit = 100, MaxHistoryLimit = 500;

    internal static async Task<Results<Ok<List<CardChangeDetail>>, NotFound>> ListCardHistory(
        Guid cardId, int? limit, PrikBoardDbContext db, CancellationToken ct)
    {
        // Deleted cards have a history too (the trash window opens them).
        if (!await db.Cards.WithDeleted().AnyAsync(c => c.Id == cardId, ct))
        {
            return TypedResults.NotFound();
        }

        var changes = await db.CardChanges.AsNoTracking()
            .Where(c => c.CardId == cardId)
            .OrderByDescending(c => c.At).ThenByDescending(c => c.Id)
            .Take(Math.Clamp(limit ?? DefaultHistoryLimit, 1, MaxHistoryLimit))
            .ToListAsync(ct);
        var ids = changes.SelectMany(CardChangeDetail.PrincipalIds).Distinct().ToList();
        var principals = ids.Count == 0
            ? []
            : await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return TypedResults.Ok(changes.Select(c => CardChangeDetail.From(c, principals)).ToList());
    }

    /// <summary>Whether the title or any of the card's content contains <paramref name="text"/>, ignoring case.</summary>
    internal static bool ContainsText(Card card, string text) =>
        card.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
        || card.Components.Any(b => b.SearchText().Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));

    internal static async Task<Results<Ok<TrashContents>, NotFound>> ListDeletedCards(
        Guid boardId, string? search, PrikBoardDbContext db, CancellationToken ct)
    {
        if (await BoardRefAsync(db, boardId, ct) is not { } board) return TypedResults.NotFound();

        var cards = await db.Cards.AsNoTracking().WithDeleted()
            .Where(n => n.BoardId == boardId && n.DeletedAt != null)
            .ToListAsync(ct);
        // Boards are small, so the search runs in memory, like listCards.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            cards = cards.Where(n => ContainsText(n, text)).ToList();
        }
        cards = cards.OrderByDescending(n => n.DeletedAt).ThenByDescending(n => n.Id).ToList();

        // The images that come back with them: deleted at the same moment.
        var stamps = cards.ToDictionary(n => n.Id, n => n.DeletedAt);
        var ids = stamps.Keys.ToList();
        var images = (await db.Images.AsNoTracking().WithDeleted().Include(i => i.AddedBy).Include(i => i.File)
                .Where(i => i.CardId != null && ids.Contains(i.CardId.Value))
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .ToListAsync(ct))
            .Where(i => i.DeletedAt == stamps[i.CardId!.Value])
            .ToList();
        var principals = await PrincipalsOnAsync(db, cards, ct);
        return TypedResults.Ok(new TrashContents(
            cards.Select(n => CardDetail.From(n, board, principals)).ToList(),
            images.Select(ImageDetail.From).ToList()));
    }

    internal static async Task<Results<Ok<CardDetail>, NotFound, ValidationProblem, ProblemHttpResult>> RestoreCard(
        Guid cardId, RestoreCardRequest? request, PrikBoardDbContext db, CancellationToken ct)
    {
        var card = await db.Cards.WithDeleted().FirstOrDefaultAsync(n => n.Id == cardId, ct);
        if (card is null) return TypedResults.NotFound();
        if (card.DeletedAt is not { } stamp) return Problems.NotDeleted("card");
        if (Problems.XWithoutY(request?.X, request?.Y) is { } apart) return apart;

        // Tags deleted meanwhile were taken off the card as well, so this only fills a gap.
        if (await TagEndpoints.EnsureBoardTagsAsync(db, card.BoardId, card.Tags, ct) is { } tooManyTags) return tooManyTags;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        card.DeletedAt = null;
        await Tombstones.RestoreWithCardAsync(db, cardId, stamp, ct);
        var stacked = await BucketEndpoints.StackAsync(db, card.BoardId, ct);
        if (request is { X: { } x, Y: { } y })
        {
            // Dropped somewhere else (out of the trash): like a move, so it comes out on top, in whatever bucket it lands in.
            (card.X, card.Y) = (Layout.Snap(x), Layout.Snap(y));
            card.UpdatedAt = DateTimeOffset.UtcNow;
            Layout.Place(card, stacked);
        }
        else
        {
            // UpdatedAt stays, so the card goes back to its place in the stacking order too.
            // Back in its bucket if that is still around it, or wherever it now lands.
            Layout.Recheck(card, stacked);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await DetailAsync(db, card, ct));
    }

    internal static async Task<Results<NoContent, NotFound, ProblemHttpResult>> PurgeCard(
        Guid boardId, Guid cardId, PrikBoardDbContext db, CancellationToken ct)
    {
        // Its images and drawings go with it through the cascades; their files at the next sweep.
        var purged = await db.Cards.WithDeleted()
            .Where(n => n.Id == cardId && n.BoardId == boardId && n.DeletedAt != null)
            .ExecuteDeleteAsync(ct);
        if (purged > 0) return TypedResults.NoContent();
        if (!await db.Cards.AnyAsync(n => n.Id == cardId && n.BoardId == boardId, ct)) return TypedResults.NotFound();
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Not deleted", detail: "The card is not in the trash; delete it first.");
    }

    internal static async Task<Ok<EmptyTrashResult>> EmptyTrash(Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var purged = await db.Cards.WithDeleted()
            .Where(n => n.BoardId == boardId && n.DeletedAt != null)
            .ExecuteDeleteAsync(ct);
        return TypedResults.Ok(new EmptyTrashResult(purged));
    }

    /// <summary>
    /// The creators and assignees of <paramref name="cards"/>, loaded in one query, so their current names can be shown.
    /// </summary>
    internal static async Task<Dictionary<Guid, Principal>> PrincipalsOnAsync(
        PrikBoardDbContext db, IEnumerable<Card> cards, CancellationToken ct)
    {
        var ids = cards.SelectMany(n => n.CreatedById is { } by ? [by, .. n.AssigneeIds] : n.AssigneeIds).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>What a card's readable id and path need from its board; null when the board does not exist.</summary>
    internal static Task<BoardRef?> BoardRefAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        db.Boards.AsNoTracking().Where(b => b.Id == boardId).Select(b => new BoardRef(b.ShortId, b.Key)).FirstOrDefaultAsync(ct);

    private static async Task<CardDetail> DetailAsync(PrikBoardDbContext db, Card card, CancellationToken ct) =>
        await DetailAsync(db, card, (await BoardRefAsync(db, card.BoardId, ct))!, ct);

    private static async Task<CardDetail> DetailAsync(PrikBoardDbContext db, Card card, BoardRef board, CancellationToken ct) =>
        CardDetail.From(card, board, await PrincipalsOnAsync(db, [card], ct));

    /// <summary>Cards with what their readable id and path need from their board, in the same query.</summary>
    private static IQueryable<CardWithBoard> WithBoardRef(IQueryable<Card> cards) =>
        cards.AsNoTracking().Select(n => new CardWithBoard(n, new BoardRef(n.Board!.ShortId, n.Board.Key)));

    private sealed record CardWithBoard(Card Card, BoardRef Board);

    /// <summary>
    /// Positions <paramref name="card"/> exactly at X and Y (joining the named bucket, or else the topmost one there), in a
    /// free spot inside the named bucket (joining it), or, with neither, in a free spot outside the buckets. Other cards keep
    /// their bucket, except loose ones that a bucket grown to make room now covers. Returns a problem for an invalid combination.
    /// </summary>
    private static ValidationProblem? Place(
        Card card, Guid? bucketId, int? x, int? y, List<Bucket> buckets, List<Card> others)
    {
        if (Problems.XWithoutY(x, y) is { } apart) return apart;
        var bucket = bucketId is { } id ? buckets.Find(b => b.Id == id) : null;
        if (bucketId is not null && bucket is null)
        {
            return Problems.Invalid("bucketId", "The bucket does not exist on this board.");
        }

        var stacked = Layout.BackToFront(buckets);
        if (x is { } exactX && y is { } exactY)
        {
            (card.X, card.Y) = (Layout.Snap(exactX), Layout.Snap(exactY));
            // Dropped where buckets overlap, it joins the one named (shown in front), not the topmost.
            if (bucket is null) Layout.Place(card, stacked);
            else if (!Layout.PlaceIn(card, bucket))
            {
                return Problems.Invalid("bucketId", "With X and Y, the bucket must contain the card's centre.");
            }
        }
        else if (bucket is not null)
        {
            (card.X, card.Y) = Layout.MakeRoomIn(bucket, others, buckets);
            // Put in this bucket on purpose, so it joins it even when it is collapsed (which other cards don't).
            card.BucketId = bucket.Id;
            // A bucket that grew to make room can now cover loose cards too.
            Layout.Adopt(bucket, stacked, others, [], looseOnly: true);
        }
        else
        {
            (card.X, card.Y) = Layout.FreeSpotOutside(buckets, others);
            Layout.Place(card, stacked);
        }
        return null;
    }

    /// <summary>The most people and agents on one card (also the MaxLength on assigneeIds).</summary>
    internal const int MaxAssignees = 10;

    /// <summary>
    /// Checks that everyone being added to a card (<paramref name="ids"/> not in <paramref name="already"/>) can be
    /// assigned on the board. People already on the card may stay after they lose access, so a client can always send the
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
        return Problems.Invalid("assigneeIds", $"{string.Join(", ", missing)} cannot be assigned: no access to this board. See people in getBoard.");
    }

    /// <summary>Replaces each list that is given (null keeps it), normalizing tags. Returns a problem for an invalid value.</summary>
    private static ValidationProblem? ApplyLists(Card card, List<string>? tags, List<Guid>? assigneeIds)
    {
        if (tags is not null)
        {
            var normalized = CardTags.Normalize(tags);
            if (normalized.Find(t => t.Length > CardTags.MaxLength) is { } tooLong)
            {
                return Problems.Invalid("tags", $"Tag \"{tooLong}\" is longer than {CardTags.MaxLength} characters.");
            }
            card.Tags = normalized;
        }
        if (assigneeIds is not null)
        {
            card.AssigneeIds = assigneeIds.Distinct().ToList();
        }
        return null;
    }
}
