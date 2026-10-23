using System.ComponentModel;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Mcp;

/// <summary>A card in a compact board overview. Empty lists and false flags are left out.</summary>
/// <param name="Id">The card id.</param>
/// <param name="ReadableId">How people refer to it, e.g. "MPCT-415": the board's key and the card's number. Tools take it wherever they take a card.</param>
/// <param name="Title">Its title.</param>
/// <param name="Color">Its fill.</param>
/// <param name="Tags">Its tags; left out when it has none.</param>
/// <param name="Assignees">The names of the people and agents on it (their ids are in the board's people); left out when nobody is.</param>
/// <param name="Checklist">Progress over all its checklists as "done/total"; left out without checklist items.</param>
/// <param name="HasContent">True when the card has content (text, checklist items or links), which get_card reads; left out otherwise.</param>
/// <param name="AgentWorking">True when an agent is working on it right now; left out otherwise.</param>
/// <param name="CreatedBy">Who created it; left out when unknown.</param>
public sealed record CardSummary(
    Guid Id, string ReadableId, string Title, CardColor Color, IReadOnlyList<string>? Tags, IReadOnlyList<string>? Assignees,
    string? Checklist, bool? HasContent, bool? AgentWorking, string? CreatedBy)
{
    public static CardSummary From(CardDetail n)
    {
        var items = n.Components.SelectMany(b => b.Items ?? []).ToList();
        var hasContent = n.Components.Any(b => !string.IsNullOrEmpty(b.Text) || b.Items is { Count: > 0 } || b.Links is { Count: > 0 });
        return new(n.Id, n.ReadableId, n.Title, n.Color,
            n.Tags.Count == 0 ? null : n.Tags,
            n.Assignees.Count == 0 ? null : n.Assignees.Select(a => a.Name).ToList(),
            items.Count == 0 ? null : $"{items.Count(i => i.Done)}/{items.Count}",
            hasContent ? true : null, n.AgentWorking ? true : null, n.CreatedBy);
    }
}

/// <summary>A bucket (column) with its cards, top to bottom.</summary>
/// <param name="Id">The bucket id.</param>
/// <param name="Name">The bucket name; tools take it as the bucket too.</param>
/// <param name="CardCount">How many cards it holds.</param>
/// <param name="Cards">Its cards, top to bottom; left out when the bucket is collapsed (search_cards with this bucket lists them) or with cards false.</param>
/// <param name="Collapsed">True when it is folded up on the board, its cards hidden there until someone opens it; left out otherwise.</param>
public sealed record BucketOverview(Guid Id, string Name, int CardCount, IReadOnlyList<CardSummary>? Cards, bool? Collapsed);

/// <summary>Someone who can be assigned to cards on the board.</summary>
/// <param name="PrincipalId">Their principal id.</param>
/// <param name="Name">Their current name; tools take it to assign them.</param>
/// <param name="Kind">Person or Agent.</param>
/// <param name="IsYou">True for you; left out for everyone else.</param>
/// <param name="OwnerId">For the agent of a personal access token: the person whose token it is.</param>
public sealed record BoardPersonSummary(Guid PrincipalId, string Name, AssigneeKind Kind, bool? IsYou, Guid? OwnerId)
{
    public static BoardPersonSummary From(BoardPerson p) => new(p.PrincipalId, p.Name, p.Kind, p.IsYou ? true : null, p.OwnerId);
}

/// <summary>A compact board: buckets in order with their cards, plus cards outside every bucket.</summary>
/// <param name="Id">The board id.</param>
/// <param name="ShortId">The board in web addresses (/#/boards/{shortId}); tools take it as the board too.</param>
/// <param name="Key">Shown before card numbers in readable ids (MPCT-415).</param>
/// <param name="Name">The board name.</param>
/// <param name="Description">The board description, if any.</param>
/// <param name="YourRole">Viewer (read only), Editor or Owner.</param>
/// <param name="Rules">The board's agent rules in Markdown, or null if none are set. Follow them in every action on this board.</param>
/// <param name="RulesUpdatedAt">When the agent rules last changed (UTC), or null if they were never set.</param>
/// <param name="Buckets">Buckets in the order they were created, each with its cards top to bottom.</param>
/// <param name="LooseCards">Cards outside every bucket; left out with cards false.</param>
/// <param name="Images">How many images the board has, on the board itself and on its cards.</param>
/// <param name="Tags">The board's tags with how many cards use each. Reuse these when tagging cards.</param>
/// <param name="People">Who can be assigned to cards; tools take their names.</param>
public sealed record BoardOverview(
    Guid Id, string ShortId, string Key, string Name, string? Description, BoardRole YourRole, string? Rules, DateTimeOffset? RulesUpdatedAt,
    IReadOnlyList<BucketOverview> Buckets,
    IReadOnlyList<CardSummary>? LooseCards, int Images, IReadOnlyList<BoardTagDetail> Tags, IReadOnlyList<BoardPersonSummary> People);

/// <summary>A board you can use.</summary>
/// <param name="Id">The board id.</param>
/// <param name="ShortId">The board in web addresses (/#/boards/{shortId}); tools take it as the board too.</param>
/// <param name="Key">Shown before card numbers in readable ids (MPCT-415).</param>
/// <param name="Name">The board name, usually the project name.</param>
/// <param name="Description">The board description; left out when there is none.</param>
/// <param name="YourRole">Viewer (read only), Editor or Owner.</param>
/// <param name="LastUsed">True for the board you used last through these tools; left out for the others.</param>
public sealed record BoardListing(Guid Id, string ShortId, string Key, string Name, string? Description, BoardRole YourRole, bool? LastUsed);

/// <summary>A card as the tools return it. Empty lists and false flags are left out.</summary>
/// <param name="Id">The card id.</param>
/// <param name="ReadableId">How people refer to it, e.g. "MPCT-415". Tools take it wherever they take a card.</param>
/// <param name="Path">Its link within the site, e.g. /#/boards/k3x9m2/cards/415; use it as a link's url to link this card from another.</param>
/// <param name="BoardId">The board it is on.</param>
/// <param name="BucketId">The bucket it is in; left out when it is loose on the board.</param>
/// <param name="Bucket">That bucket's name.</param>
/// <param name="Title">Its title.</param>
/// <param name="Color">Its fill.</param>
/// <param name="Tags">Its tags; left out when it has none.</param>
/// <param name="Assignees">The names of the people and agents on it; left out when nobody is.</param>
/// <param name="AgentWorking">True when an agent is working on it right now; left out otherwise.</param>
/// <param name="Components">Its content: Text, Checklist and Links components, each with its id (and, from get_card, its content).</param>
/// <param name="CreatedBy">Who created it, when known.</param>
/// <param name="CreatedAt">When it was created (UTC); from get_card.</param>
/// <param name="DeletedAt">When it was deleted (UTC), for cards in the trash.</param>
public sealed record CardView(
    Guid Id, string ReadableId, string Path, Guid BoardId, Guid? BucketId, string? Bucket, string Title, CardColor Color,
    IReadOnlyList<string>? Tags, IReadOnlyList<string>? Assignees, bool? AgentWorking, IReadOnlyList<ComponentView>? Components,
    string? CreatedBy, DateTimeOffset? CreatedAt, DateTimeOffset? DeletedAt)
{
    /// <summary>The card, with its components' content when <paramref name="content"/> (get_card), or only their ids and titles (writes).</summary>
    public static CardView From(CardDetail n, string? bucket, bool content) => new(
        n.Id, n.ReadableId, n.Path, n.BoardId, n.BucketId, bucket, n.Title, n.Color,
        n.Tags.Count == 0 ? null : n.Tags,
        n.Assignees.Count == 0 ? null : n.Assignees.Select(a => a.Name).ToList(),
        n.AgentWorking ? true : null,
        n.Components.Count == 0 ? null : n.Components.Select(c => content ? ComponentView.From(c) : ComponentView.Brief(c)).ToList(),
        n.CreatedBy, content ? n.CreatedAt : null, n.DeletedAt);
}

/// <summary>A component of a card: a Text, Checklist or Links component.</summary>
/// <param name="Id">The component id; tools also take its title, or its kind when the card has one of that kind.</param>
/// <param name="Kind">Text, Checklist or Links.</param>
/// <param name="Title">Its heading, if any.</param>
/// <param name="Text">The text of a Text component, in Markdown.</param>
/// <param name="Items">The items of a Checklist component.</param>
/// <param name="Links">The links of a Links component.</param>
public sealed record ComponentView(Guid Id, CardComponentKind Kind, string? Title, string? Text, IReadOnlyList<ChecklistItem>? Items, IReadOnlyList<CardLink>? Links)
{
    public static ComponentView From(CardComponentDetail c) => new(c.Id, c.Kind, c.Title, c.Text, c.Items, c.Links);

    public static ComponentView Brief(CardComponentDetail c) => new(c.Id, c.Kind, c.Title, null, null, null);
}

/// <summary>A new card for create_cards.</summary>
public sealed record NewCard(
    [property: Description(BoardTools.TitleHelp)] string Title,
    [property: Description(BoardTools.BodyHelp)] string? Body = null,
    [property: Description(BoardTools.NewCardBucketHelp)] string? Bucket = null,
    [property: Description(BoardTools.NewCardColorHelp)] CardColor? Color = null,
    [property: Description(BoardTools.NewCardTagsHelp)] List<string>? Tags = null,
    [property: Description(BoardTools.NewCardAssigneesHelp)] List<string>? Assignees = null,
    [property: Description(BoardTools.ChecklistHelp)] List<ChecklistItem>? Checklist = null,
    [property: Description(BoardTools.NewCardLinksHelp)] List<CardLink>? Links = null,
    [property: Description(BoardTools.WorkingHelp)] bool Working = false);

/// <summary>A board's agent rules.</summary>
/// <param name="BoardId">The board id.</param>
/// <param name="Rules">The rules in Markdown, or null if the board has none.</param>
/// <param name="UpdatedAt">When the rules last changed (UTC), or null if they were never set.</param>
/// <param name="Note">How to treat the rules.</param>
public sealed record BoardRules(Guid BoardId, string? Rules, DateTimeOffset? UpdatedAt, string Note);
