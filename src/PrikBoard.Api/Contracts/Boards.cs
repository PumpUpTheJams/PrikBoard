using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A board without its buckets and notes.</summary>
/// <param name="Id">Board identifier.</param>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board name or description last changed (UTC).</param>
/// <param name="YourRole">Your role on the board: Viewer, Editor or Owner.</param>
public record BoardSummary(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, BoardRole YourRole)
{
    public static BoardSummary From(Board board, BoardRole role) =>
        new(board.Id, board.Name, board.Description, board.CreatedAt, board.UpdatedAt, role);
}

/// <summary>A full board snapshot: every bucket, note and image with its position on the canvas.</summary>
/// <param name="Id">Board identifier.</param>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board name or description last changed (UTC).</param>
/// <param name="Buckets">Buckets in stacking order, back to front (oldest first).</param>
/// <param name="Notes">Notes grouped by bucket in the order of Buckets, loose notes last, each group top to bottom and left to right.</param>
/// <param name="Images">Images on the canvas and attached to notes (see noteId), oldest first. Download them from contentUrl.</param>
/// <param name="Tags">The tags defined on the board, by name, with how many notes use each. Notes pick their tags from this list.</param>
/// <param name="People">Who can be assigned to notes: members, and the agents of members' access tokens that reach the board. You first, then people, then agents.</param>
/// <param name="YourRole">Your role on the board: Viewer (read only), Editor or Owner.</param>
public record BoardDetail(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BucketDetail> Buckets,
    IReadOnlyList<NoteDetail> Notes,
    IReadOnlyList<ImageDetail> Images,
    IReadOnlyList<BoardTagDetail> Tags,
    IReadOnlyList<BoardPerson> People,
    BoardRole YourRole)
{
    /// <summary>
    /// Maps a board with its buckets, notes and images; <paramref name="principals"/> holds the notes' creators and
    /// assignees, <paramref name="people"/> who can be assigned (see BoardAccess.PeopleAsync).
    /// </summary>
    public static BoardDetail From(
        Board board, BoardRole role, IReadOnlyDictionary<Guid, Principal> principals, IEnumerable<Principal> people, Guid caller) =>
        new(board.Id, board.Name, board.Description, board.CreatedAt, board.UpdatedAt,
            Layout.BackToFront(board.Buckets).Select(BucketDetail.From).ToList(),
            Layout.ReadingOrder(board.Notes, board.Buckets).Select(n => NoteDetail.From(n, principals)).ToList(),
            board.Images.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).Select(ImageDetail.From).ToList(),
            BoardTagDetail.List(board.Tags, board.Notes),
            BoardPerson.List(people, caller),
            role);
}

/// <summary>Someone who can be assigned to notes on a board.</summary>
/// <param name="PrincipalId">Their principal id, for assigneeIds in createNote and updateNote.</param>
/// <param name="Name">Their current display name.</param>
/// <param name="Kind">Person or Agent.</param>
/// <param name="IsYou">Whether this is the caller.</param>
/// <param name="OwnerId">For the agent of a personal access token: the person whose token it is. Null otherwise.</param>
public record BoardPerson(Guid PrincipalId, string Name, AssigneeKind Kind, bool IsYou, Guid? OwnerId)
{
    /// <summary>You first, then people before agents, each by name.</summary>
    public static List<BoardPerson> List(IEnumerable<Principal> people, Guid caller) => people
        .DistinctBy(p => p.Id)
        .Select(p => new BoardPerson(p.Id, p.ShownName, Assignee.KindOf(p), p.Id == caller, (p as Agent)?.OwnerId))
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
public record UpdateBoardRequest(
    [property: StringLength(100, MinimumLength = 1)] string? Name = null,
    [property: StringLength(1000)] string? Description = null);
