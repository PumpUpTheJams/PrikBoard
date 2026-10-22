using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// The components on a card's own canvas. Each write locks the card's row, so people editing different components of the same
/// card at once never overwrite each other's changes.
/// </summary>
public static class CardComponentEndpoints
{
    public static RouteGroupBuilder MapCardComponentEndpoints(this RouteGroupBuilder api)
    {
        var components = api.MapGroup("/cards/{cardId:guid}/components").WithTags("Cards");

        components.MapPost("/", AddCardComponent)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("addCardComponent")
            .WithSummary("Add a component to a card")
            .WithDescription("Adds a text, checklist or links component to the card's content. Without x and y it goes in the first " +
                             $"free spot on the card's canvas. A card has at most {CardComponent.MaxComponents} components. " +
                             "To put back a removed component, send it with the id it had.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        components.MapPatch("/{componentId:guid}", UpdateCardComponent)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("updateCardComponent")
            .WithSummary("Edit, move or resize a component")
            .WithDescription("Changes any of a component's title, content (text, checklist items or links, whichever fits its kind), " +
                             "position and size. Omitted fields stay unchanged; a list that is given replaces the component's whole list. " +
                             "appendText, addItems, checkItems and uncheckItems change a Text or Checklist component without sending its content back.")
            .ProducesValidationProblem();

        components.MapDelete("/{componentId:guid}", DeleteCardComponent)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Card)
            .WithName("deleteCardComponent")
            .WithSummary("Remove a component from a card");

        return api;
    }

    internal static async Task<Results<Created<CardComponentDetail>, NotFound, ValidationProblem, ProblemHttpResult>> AddCardComponent(
        Guid cardId, NewCardComponent request, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedCardAsync(db, cardId, ct) is not { } card) return TypedResults.NotFound();
        if (card.Components.Count >= CardComponent.MaxComponents)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many components",
                detail: $"The card already has {CardComponent.MaxComponents} components; remove one first.");
        }

        var taken = Layout.TakenOnCard(card, await db.Images.AsNoTracking().Where(i => i.CardId == cardId).ToListAsync(ct));
        if (Build(request, taken, "") is not { } component) return InvalidComponent(request, "");
        if (Check(component, "") is { } invalid) return invalid;
        await ShortenLinksAsync(db, card.BoardId, [component], ct);
        if (request.Id is { } id)
        {
            if (card.Components.Exists(b => b.Id == id))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Component exists",
                    detail: "The card already has a component with this id.");
            }
            component.Id = id;
        }

        card.Components.Add(component);
        card.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/cards/{cardId}/components/{component.Id}", CardComponentDetail.From(component));
    }

    internal static async Task<Results<Ok<CardComponentDetail>, NotFound, ValidationProblem>> UpdateCardComponent(
        Guid cardId, Guid componentId, UpdateCardComponentRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedCardAsync(db, cardId, ct) is not { } card) return TypedResults.NotFound();
        if (card.Components.Find(b => b.Id == componentId) is not { } component) return TypedResults.NotFound();

        if (Misfit(component.Kind, request.Text, request.Items, request.Links, "") is { } misfit) return misfit;
        component.Title = Clean(PatchText.Apply(component.Title, request.Title));
        if (component.Kind == CardComponentKind.Text) component.Text = PatchText.Apply(component.Text, request.Text);
        if (request.Items is not null) component.Items = Items(request.Items);
        if (request.Links is not null) component.Links = Links(request.Links);
        if (ApplyIncrements(component, request) is { } increment) return increment;
        component.X = request.X ?? component.X;
        component.Y = request.Y ?? component.Y;
        component.Width = request.Width ?? component.Width;
        component.Height = request.Height ?? component.Height;
        Layout.Clamp(component);
        if (Check(component, "") is { } invalid) return invalid;
        if (request.Links is not null) await ShortenLinksAsync(db, card.BoardId, [component], ct);

        card.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(CardComponentDetail.From(component));
    }

    internal static async Task<Results<NoContent, NotFound>> DeleteCardComponent(
        Guid cardId, Guid componentId, PrikBoardDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await LockedCardAsync(db, cardId, ct) is not { } card) return TypedResults.NotFound();
        if (card.Components.RemoveAll(b => b.Id == componentId) == 0) return TypedResults.NotFound();

        card.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Builds the components of a new card, laying out the ones without a position. Returns a problem (keyed on
    /// "components[i].field", after <paramref name="keyPrefix"/>) for content that does not fit a component's kind or an invalid link.
    /// </summary>
    internal static ValidationProblem? BuildAll(Card card, IReadOnlyList<NewCardComponent> requests, string keyPrefix = "")
    {
        var taken = Layout.TakenOnCard(card, []);
        for (var i = 0; i < requests.Count; i++)
        {
            var prefix = $"{keyPrefix}components[{i}].";
            if (Build(requests[i], taken, prefix) is not { } component) return InvalidComponent(requests[i], prefix);
            if (Check(component, prefix) is { } invalid) return invalid;
            card.Components.Add(component);
            taken.Add((component.X, component.Y, component.Width, component.Height));
        }
        return null;
    }

    /// <summary>A component from a request, placed in a free spot without X and Y; null when the request does not fit its kind.</summary>
    private static CardComponent? Build(NewCardComponent request, IReadOnlyCollection<(int X, int Y, int Width, int Height)> taken, string prefix)
    {
        if (request.Kind is not { } kind || Misfit(kind, request.Text, request.Items, request.Links, prefix) is not null
            || request.X is null != request.Y is null)
        {
            return null;
        }

        var (width, height) = Layout.DefaultComponentSize(kind);
        var component = new CardComponent
        {
            Kind = kind,
            Title = Clean(PatchText.Apply(null, request.Title)),
            Text = kind == CardComponentKind.Text ? PatchText.Apply(null, request.Text) : null,
            Items = Items(request.Items ?? []),
            Links = Links(request.Links ?? []),
            Width = request.Width ?? width,
            Height = request.Height ?? height,
        };
        Layout.Clamp(component);
        (component.X, component.Y) = request.X is { } x && request.Y is { } y ? (x, y) : Layout.NextCardSpot(taken, component.Width, component.Height);
        Layout.Clamp(component);
        return component;
    }

    /// <summary>
    /// Applies appendText, addItems, checkItems and uncheckItems, which change part of a component's content in place. Returns a
    /// problem when they don't fit its kind, the result is too long, or an item to tick doesn't exist.
    /// </summary>
    private static ValidationProblem? ApplyIncrements(CardComponent component, UpdateCardComponentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AppendText))
        {
            if (component.Kind != CardComponentKind.Text) return Problems.Invalid("appendText", $"A {component.Kind} component has no text; use a Text component.");
            var added = request.AppendText.Trim();
            var text = string.IsNullOrWhiteSpace(component.Text) ? added : $"{component.Text.TrimEnd()}\n\n{added}";
            if (text.Length > CardComponent.MaxTextLength)
            {
                return Problems.Invalid("appendText", $"The text would be longer than {CardComponent.MaxTextLength} characters.");
            }
            component.Text = text;
        }

        if (request is { AddItems: null, CheckItems: null, UncheckItems: null }) return null;
        if (component.Kind != CardComponentKind.Checklist)
        {
            return Problems.Invalid(request.AddItems is not null ? "addItems" : request.CheckItems is not null ? "checkItems" : "uncheckItems",
                $"A {component.Kind} component has no checklist items; use a Checklist component.");
        }
        if (request.AddItems is not null) component.Items = [.. component.Items, .. Items(request.AddItems)];
        if (component.Items.Count > CardComponent.MaxItems)
        {
            return Problems.Invalid("addItems", $"A checklist has at most {CardComponent.MaxItems} items.");
        }
        return Tick(component, request.CheckItems, true, "checkItems") ?? Tick(component, request.UncheckItems, false, "uncheckItems");
    }

    /// <summary>Ticks (or unticks) the items with these texts; a problem naming the items when one isn't there.</summary>
    private static ValidationProblem? Tick(CardComponent component, List<string>? texts, bool done, string field)
    {
        foreach (var text in texts ?? [])
        {
            var matches = component.Items.Where(i => i.Text.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                return Problems.Invalid(field, $"No checklist item \"{text.Trim()}\". Items: {string.Join(", ", component.Items.Select(i => $"\"{i.Text}\""))}.");
            }
            foreach (var item in matches) item.Done = done;
        }
        return null;
    }

    /// <summary>Why <see cref="Build"/> returned null.</summary>
    private static ValidationProblem InvalidComponent(NewCardComponent request, string prefix) =>
        request.Kind is not { } kind ? Problems.Invalid(prefix + "kind", "Give the kind: Text, Checklist or Links.")
        : Misfit(kind, request.Text, request.Items, request.Links, prefix)
          ?? Problems.Invalid(prefix + (request.X is null ? "x" : "y"), "Give X and Y together.");

    /// <summary>A problem when content is given that does not fit a component of <paramref name="kind"/>.</summary>
    private static ValidationProblem? Misfit(CardComponentKind kind, string? text, List<ChecklistItem>? items, List<CardLink>? links, string prefix) =>
        kind != CardComponentKind.Text && !string.IsNullOrEmpty(text) ? Problems.Invalid(prefix + "text", $"A {kind} component has no text; use a Text component.")
        : kind != CardComponentKind.Checklist && items is { Count: > 0 } ? Problems.Invalid(prefix + "items", $"A {kind} component has no checklist items; use a Checklist component.")
        : kind != CardComponentKind.Links && links is { Count: > 0 } ? Problems.Invalid(prefix + "links", $"A {kind} component has no links; use a Links component.")
        : null;

    private static ValidationProblem? Check(CardComponent component, string prefix) =>
        component.Links.Find(l => !CardLinks.IsValid(l.Url)) is { } bad
            ? Problems.Invalid(prefix + "links", $"\"{bad.Url}\" is neither an absolute http or https URL nor a card's path (/#/boards/{{shortId}}/cards/{{number}}).")
            : null;

    /// <summary>
    /// Rewrites links in the older form (/#/boards/{boardId}/cards/{cardId}) to cards on <paramref name="boardId"/> into
    /// those cards' paths, deleted cards included. Links to other boards stay as they are, so a link says nothing new about
    /// a board the caller may not see.
    /// </summary>
    internal static async Task ShortenLinksAsync(PrikBoardDbContext db, Guid boardId, IEnumerable<CardComponent> components, CancellationToken ct)
    {
        var links = new List<(LinkEntry Link, Guid CardId)>();
        foreach (var link in components.SelectMany(b => b.Links))
        {
            if (CardLinks.TryParse(link.Url, out var target) && !target.ByNumber) links.Add((link, target.CardId));
        }
        if (links.Count == 0) return;

        var ids = links.Select(l => l.CardId).Distinct().ToList();
        var paths = await db.Cards.AsNoTracking().WithDeleted()
            .Where(n => n.BoardId == boardId && ids.Contains(n.Id))
            .Select(n => new { n.Id, n.Number, n.Board!.ShortId })
            .ToDictionaryAsync(n => n.Id, n => CardLinks.CardPath(n.ShortId, n.Number), ct);
        foreach (var (link, cardId) in links)
        {
            if (paths.TryGetValue(cardId, out var path)) link.Url = path;
        }
    }

    private static string? Clean(string? title) => title?.Trim();

    private static List<ChecklistEntry> Items(IEnumerable<ChecklistItem> items) =>
        items.Select(c => new ChecklistEntry { Text = c.Text.Trim(), Done = c.Done }).ToList();

    private static List<LinkEntry> Links(IEnumerable<CardLink> links) =>
        links.Select(l => new LinkEntry { Title = l.Title.Trim(), Url = l.Url.Trim() }).ToList();

    /// <summary>Locks the card's row until the transaction ends, then loads it; null when it does not exist.</summary>
    private static Task<Card?> LockedCardAsync(PrikBoardDbContext db, Guid cardId, CancellationToken ct) =>
        db.Cards.FromSql($"""SELECT * FROM "Cards" WHERE "Id" = {cardId} FOR UPDATE""").FirstOrDefaultAsync(ct);
}
