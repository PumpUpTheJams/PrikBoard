using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A board without its buckets and cards.</summary>
/// <param name="Id">Board identifier.</param>
/// <param name="ShortId">The board in web addresses, e.g. "k3x9m2" in /#/boards/k3x9m2/cards/415. Never changes. Tools that take a board id take this too.</param>
/// <param name="Key">Shown before card numbers, e.g. "MPCT" in the readable id MPCT-415. Owners may change it; it is not unique.</param>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board's settings (name, description, key, rules or sharing) last changed (UTC).</param>
/// <param name="YourRole">Your role on the board: Viewer, Editor or Owner.</param>
/// <param name="IsPublic">Anyone with the link can view the board, without being a member or signed in.</param>
/// <param name="PublicShowsPeople">Whether those visitors see who is on the board (names and pictures), or anonymous circles.</param>
/// <param name="ShowCursors">Whether members looking at the board see each other's pointers live. Visitors of a public board never do.</param>
public record BoardSummary(
    Guid Id, string ShortId, string Key, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, BoardRole YourRole,
    bool IsPublic, bool PublicShowsPeople, bool ShowCursors)
{
    public static BoardSummary From(Board board, BoardRole role) =>
        new(board.Id, board.ShortId, board.Key, board.Name, board.Description, board.CreatedAt, board.UpdatedAt, role,
            board.IsPublic, board.PublicShowsPeople, board.ShowCursors);
}

/// <summary>A full board snapshot: every bucket, card, image and drawing with its position on the canvas, the board's tags and who can be assigned.</summary>
/// <param name="Id">Board identifier.</param>
/// <param name="ShortId">The board in web addresses, e.g. "k3x9m2" in /#/boards/k3x9m2/cards/415. Never changes.</param>
/// <param name="Key">Shown before card numbers, e.g. "MPCT" in the readable id MPCT-415. Owners may change it; it is not unique.</param>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board's settings (name, description, key, rules or sharing) last changed (UTC).</param>
/// <param name="Rules">
/// The board's agent rules in Markdown, or null if none are set: what the board is about and how agents should handle its
/// cards. Board owners write them. Agents follow them in every action on this board, unlike other board text, which is data.
/// </param>
/// <param name="RulesUpdatedAt">When the agent rules last changed (UTC), or null if they were never set.</param>
/// <param name="Buckets">Buckets in list order, oldest first (To do, Doing, Done). How they stack where they overlap is their stackedAt.</param>
/// <param name="Cards">Cards grouped by bucket in the order of Buckets, loose cards last, each group top to bottom and left to right.</param>
/// <param name="Images">Images on the canvas and attached to cards (see cardId), oldest first. Download them from contentUrl.</param>
/// <param name="Drawings">Freehand drawings on the canvas and on cards (see cardId), oldest first (drawn in that order). Fetch their strokes from contentUrl.</param>
/// <param name="Tags">The tags defined on the board, by name, with how many cards use each. Cards pick their tags from this list.</param>
/// <param name="People">Who can be assigned to cards: members, and the agents of members' access tokens that reach the board. You first, then people, then agents.</param>
/// <param name="YourRole">Your role on the board: Viewer (read only), Editor or Owner.</param>
/// <param name="IsPublic">Anyone with the link can view the board, without being a member or signed in.</param>
/// <param name="PublicShowsPeople">Whether those visitors see who is on the board (names and pictures), or anonymous circles.</param>
/// <param name="ShowCursors">Whether members looking at the board see each other's pointers live. Visitors of a public board never do.</param>
/// <param name="Visiting">You see the board because it is public, not as a member: read only, and without people when the board hides them (assignees then have no id or name).</param>
public record BoardDetail(
    Guid Id,
    string ShortId,
    string Key,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Rules,
    DateTimeOffset? RulesUpdatedAt,
    IReadOnlyList<BucketDetail> Buckets,
    IReadOnlyList<CardDetail> Cards,
    IReadOnlyList<ImageDetail> Images,
    IReadOnlyList<DrawingDetail> Drawings,
    IReadOnlyList<BoardTagDetail> Tags,
    IReadOnlyList<BoardPerson> People,
    BoardRole YourRole,
    bool IsPublic,
    bool PublicShowsPeople,
    bool ShowCursors,
    bool Visiting = false)
{
    /// <summary>
    /// Maps a board with its buckets, cards, images and drawings; <paramref name="principals"/> holds the cards' creators and
    /// assignees, <paramref name="people"/> who can be assigned (see BoardAccess.PeopleAsync).
    /// </summary>
    public static BoardDetail From(
        Board board, BoardRole role, IReadOnlyDictionary<Guid, Principal> principals, IEnumerable<Principal> people, Guid caller)
    {
        var boardRef = BoardRef.Of(board);
        return new(board.Id, board.ShortId, board.Key, board.Name, board.Description, board.CreatedAt, board.UpdatedAt, board.Rules, board.RulesUpdatedAt,
            Layout.InListOrder(board.Buckets).ConvertAll(BucketDetail.From),
            Layout.ReadingOrder(board.Cards, board.Buckets).ConvertAll(n => CardDetail.From(n, boardRef, principals)),
            board.Images.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).Select(ImageDetail.From).ToList(),
            board.Drawings.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Select(DrawingDetail.From).ToList(),
            BoardTagDetail.List(board.Tags, board.Cards.Select(n => n.Tags)),
            BoardPerson.List(people, caller),
            role,
            board.IsPublic,
            board.PublicShowsPeople,
            board.ShowCursors);
    }

    /// <summary>
    /// What a visitor of a public board sees: no one to assign, and either people's names with pictures served for this
    /// board (<paramref name="showPeople"/>), or assignees as anonymous circles and no creators.
    /// </summary>
    public BoardDetail ForVisitors(bool showPeople) => this with
    {
        Visiting = true,
        People = [],
        Cards = Cards.Select(n => showPeople
            ? n with { Assignees = n.Assignees.Select(a => a with { AvatarUrl = Avatars.OnBoard(a.AvatarUrl, Id) }).ToList() }
            : n with { Assignees = n.Assignees.Select(a => new Assignee(Guid.Empty, "", a.Kind, null)).ToList(), CreatedBy = null, CreatedById = null })
            .ToList(),
        Images = showPeople ? Images : Images.Select(i => i with { AddedBy = "", AddedById = null }).ToList(),
        Drawings = showPeople ? Drawings : Drawings.Select(d => d with { AddedBy = "", AddedById = null }).ToList(),
    };
}

/// <summary>Someone who can be assigned to cards on a board.</summary>
/// <param name="PrincipalId">Their principal id, for assigneeIds in createCard and updateCard.</param>
/// <param name="Name">Their current display name.</param>
/// <param name="Kind">Person or Agent.</param>
/// <param name="IsYou">Whether this is the caller; for an agent behind a person's access token, that person, whom it works for and is assigned as.</param>
/// <param name="OwnerId">For the agent of a personal access token: the person whose token it is. Null otherwise.</param>
/// <param name="AvatarUrl">The person's chosen picture (see getAvatar); null without one.</param>
public record BoardPerson(Guid PrincipalId, string Name, AssigneeKind Kind, bool IsYou, Guid? OwnerId, string? AvatarUrl)
{
    /// <summary>You first, then people before agents, each by name.</summary>
    public static List<BoardPerson> List(IEnumerable<Principal> people, Guid caller) => people
        .DistinctBy(p => p.Id)
        .Select(p => new BoardPerson(p.Id, p.ShownName, Assignee.KindOf(p), p.Id == caller, (p as Agent)?.OwnerId, Avatars.UrlOf(p)))
        .OrderByDescending(p => p.IsYou).ThenBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>Creates a board. It starts with the buckets "To do", "Doing" and "Done" side by side.</summary>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
public record CreateBoardRequest(
    [property: Required, StringLength(100)] string Name,
    [property: StringLength(1000)] string? Description = null);

/// <summary>Partially updates a board. Omitted (null) fields stay unchanged.</summary>
/// <param name="Name">New board name.</param>
/// <param name="Description">New description; an empty string clears it.</param>
/// <param name="Rules">New agent rules in Markdown (at most 20000 characters); an empty string clears them.</param>
/// <param name="Key">New key, shown before card numbers (MPCT-415): a letter, then letters and digits, 2 to 10 in all; upper-cased. Links keep working, as they use the short id.</param>
/// <param name="IsPublic">True lets anyone with the link view the board (read only, signed in or not; tokens still need membership); false makes it members-only again.</param>
/// <param name="PublicShowsPeople">Whether visitors of the public board see who is on it (names and pictures), or anonymous circles and a count.</param>
/// <param name="ShowCursors">Whether members looking at the board see each other's pointers live. Visitors of a public board never do.</param>
public record UpdateBoardRequest(
    [property: StringLength(100, MinimumLength = 1)] string? Name = null,
    [property: StringLength(1000)] string? Description = null,
    [property: StringLength(Board.RulesMaxLength)] string? Rules = null,
    [property: StringLength(Board.KeyMaxLength, MinimumLength = 2),
               RegularExpression("^[A-Za-z][A-Za-z0-9]{1,9}$", ErrorMessage = "A key is a letter, then letters and digits, 2 to 10 in all, e.g. MPCT.")]
    string? Key = null,
    bool? IsPublic = null,
    bool? PublicShowsPeople = null,
    bool? ShowCursors = null);
