using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A card. Every card is 200 by 120 pixels.</summary>
/// <param name="Id">Card identifier.</param>
/// <param name="Number">The card's number on its board: 1 for the first card, never reused. Part of its path and readable id.</param>
/// <param name="ReadableId">How to refer to the card in text: the board's key and the card's number, e.g. "MPCT-415". MCP tools take it wherever they take a card. The key may change; the number does not.</param>
/// <param name="BoardId">Board the card is stuck on.</param>
/// <param name="BucketId">Bucket containing the card's centre, or null when the card is loose on the board.</param>
/// <param name="Title">Short title shown on the card.</param>
/// <param name="Color">Card fill.</param>
/// <param name="Tags">Names of the board's tags on the card, shown on the card, e.g. "backend".</param>
/// <param name="Assignees">People and agents working on the card, in display order, with their current names.</param>
/// <param name="Components">The card's content, shown when the card is expanded: text, checklists and lists of links, laid out on the card's own canvas. Change them with addCardComponent, updateCardComponent and deleteCardComponent.</param>
/// <param name="AgentWorking">True while an AI agent is actively working on the card. The board shows a lilac halo around it.</param>
/// <param name="X">Left edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Y">Top edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="CreatedBy">Current display name of whoever stuck the card on the board; null for cards from before this was recorded, or once they no longer exist.</param>
/// <param name="CreatedById">Principal id of whoever created the card, or null (see createdBy).</param>
/// <param name="CreatedAt">When the card was created (UTC).</param>
/// <param name="UpdatedAt">When the card was last edited or moved (UTC). The most recently updated card is drawn on top.</param>
/// <param name="Path">Where the web app shows this card, relative to the site, e.g. "/#/boards/k3x9m2/cards/415" (the board's short id and the card's number). It never changes. Use it as a link's url to link this card from another card, and getCardByNumber to look it up.</param>
/// <param name="DeletedAt">When the card was deleted (UTC), for cards in the trash (listDeletedCards); null otherwise.</param>
public record CardDetail(
    Guid Id,
    int Number,
    string ReadableId,
    Guid BoardId,
    Guid? BucketId,
    string Title,
    CardColor Color,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Assignee> Assignees,
    IReadOnlyList<CardComponentDetail> Components,
    bool AgentWorking,
    int X,
    int Y,
    string? CreatedBy,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Path,
    DateTimeOffset? DeletedAt = null)
{
    /// <summary>
    /// Maps a card on <paramref name="board"/>; <paramref name="principals"/> holds its creator and assignees (see
    /// CardEndpoints.PrincipalsOnAsync).
    /// </summary>
    public static CardDetail From(Card card, BoardRef board, IReadOnlyDictionary<Guid, Principal> principals) =>
        new(card.Id, card.Number, CardLinks.ReadableId(board.Key, card.Number), card.BoardId, card.BucketId, card.Title, card.Color,
            card.Tags,
            card.AssigneeIds.Select(id => Assignee.Of(id, principals)).ToList(),
            card.Components.Select(CardComponentDetail.From).ToList(),
            card.AgentWorking, card.X, card.Y,
            card.CreatedById is { } by && principals.TryGetValue(by, out var creator) ? creator.ShownName : null,
            card.CreatedById, card.CreatedAt, card.UpdatedAt,
            CardLinks.CardPath(board.ShortId, card.Number), card.DeletedAt);
}

/// <summary>A person or AI agent assigned to a card.</summary>
/// <param name="PrincipalId">Their principal id: what assigneeIds in createCard and updateCard take (see people in getBoard).</param>
/// <param name="Name">Their current display name, e.g. "Jaap V." or "Claude Code"; "Unknown" once they no longer exist. People are shown by their initials.</param>
/// <param name="Kind">Person or Agent. Agents are first-class assignees that can own work.</param>
/// <param name="AvatarUrl">The person's chosen picture (see getAvatar), shown instead of their initials; null without one.</param>
public record Assignee(Guid PrincipalId, string Name, AssigneeKind Kind, string? AvatarUrl)
{
    public static Assignee Of(Guid id, IReadOnlyDictionary<Guid, Principal> principals) =>
        principals.TryGetValue(id, out var principal)
            ? new(id, principal.ShownName, KindOf(principal), Avatars.UrlOf(principal))
            : new(id, "Unknown", AssigneeKind.Person, null);

    public static AssigneeKind KindOf(Principal principal) => principal is Agent ? AssigneeKind.Agent : AssigneeKind.Person;
}

/// <summary>One checklist item, e.g. an acceptance criterion.</summary>
/// <param name="Text">What needs to be true or done.</param>
/// <param name="Done">Whether it is ticked off.</param>
public record ChecklistItem(
    [property: Required, StringLength(300, MinimumLength = 1)] string Text,
    bool Done = false);

/// <summary>A link related to a card: a web page, or another card.</summary>
/// <param name="Title">Text shown for the link, e.g. "Retry-After handling (PR #415)".</param>
/// <param name="Url">An absolute http or https address, or another card's path (path in getCard), e.g. "/#/boards/k3x9m2/cards/415". The older form "/#/boards/{boardId}/cards/{cardId}" works too and is stored as the card's path. The web app shows a linked card with its current title.</param>
public record CardLink(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: Required, StringLength(2000)] string Url);

/// <summary>
/// Sticks a new card on a board. Give a bucketId to drop it in a free spot inside that bucket, or X and Y to place it exactly.
/// With neither, it goes into the board's first bucket (or a free spot on the board when there are no buckets).
/// A full bucket grows to make room.
/// </summary>
/// <param name="Title">Short title shown on the card; trimmed. May be empty: a card without a title shows none.</param>
/// <param name="Color">Card fill; defaults to Butter.</param>
/// <param name="BucketId">Bucket to put the card in, in a free spot. With X and Y: the bucket it joins where buckets overlap there, which must contain the card's centre (without it, the topmost one does).</param>
/// <param name="X">Left edge in canvas pixels; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge in canvas pixels; snapped to the 20 px grid. Give it together with X.</param>
/// <param name="Tags">Tag names; trimmed, lower-cased, hyphenated and de-duplicated, each at most 30 characters. Prefer the board's tags (tags in getBoard); new names are added to the board.</param>
/// <param name="AssigneeIds">Principal ids of people and agents to assign, from people in getBoard (at most 10).</param>
/// <param name="Components">Initial content: text, checklists and lists of links. Components without X and Y are laid out automatically.</param>
/// <param name="AgentWorking">Set to true when the calling agent starts working on the card right away.</param>
public record CreateCardRequest(
    [property: Required(AllowEmptyStrings = true), StringLength(200)] string Title,
    CardColor? Color = null,
    Guid? BucketId = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null,
    [property: MaxLength(10)] List<string>? Tags = null,
    [property: MaxLength(10)] List<Guid>? AssigneeIds = null,
    [property: MaxLength(CardComponent.MaxComponents)] List<NewCardComponent>? Components = null,
    bool? AgentWorking = null);

/// <summary>Creates several cards at once (createCards), in the order given.</summary>
/// <param name="Cards">The cards, each as createCard takes one; at most 50.</param>
public record CreateCardsRequest([property: Required, MaxLength(Endpoints.CardEndpoints.MaxBatch)] List<CreateCardRequest> Cards);

/// <summary>
/// Partially updates a card. Omitted (null) fields stay unchanged. A list that is given replaces the whole list (send []
/// to clear it). Use moveCard to move the card, and addCardComponent, updateCardComponent and deleteCardComponent to change its content.
/// </summary>
/// <param name="Title">New title; trimmed. Empty or whitespace clears it.</param>
/// <param name="Color">New fill.</param>
/// <param name="Tags">Replacement tag names; new names are added to the board.</param>
/// <param name="AssigneeIds">Replacement assignees by principal id (see people in getBoard). People already on the card may stay after they lose access to the board.</param>
/// <param name="AgentWorking">True when an agent starts working on the card, false when it stops.</param>
public record UpdateCardRequest(
    [property: StringLength(200)] string? Title = null,
    CardColor? Color = null,
    [property: MaxLength(10)] List<string>? Tags = null,
    [property: MaxLength(10)] List<Guid>? AssigneeIds = null,
    bool? AgentWorking = null);

/// <summary>Says the caller is (or is no longer) working on a card.</summary>
/// <param name="Working">True when you start: you are assigned (by your principal id) and the board shows a halo around the card. False when you stop: the halo goes, you stay assigned.</param>
public record SetWorkingRequest(bool Working);

/// <summary>
/// Moves a card. Give a bucketId (the card lands in a free spot inside that bucket, which grows when full) or X and Y
/// (the card goes exactly there and belongs to the topmost bucket containing its centre, or none). Give both to put it
/// exactly there in that bucket, where buckets overlap: the bucket must contain the card's centre.
/// </summary>
/// <param name="BucketId">Destination bucket on the same board; with X and Y, the one of the buckets there it joins.</param>
/// <param name="X">New left edge in canvas pixels; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge in canvas pixels; snapped to the 20 px grid.</param>
public record MoveCardRequest(
    Guid? BucketId = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? X = null,
    [property: Range(Layout.MinCoordinate, Layout.MaxCoordinate)] int? Y = null);
