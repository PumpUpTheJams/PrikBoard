using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A board as an admin sees it: its name and who is on it, never its content.</summary>
/// <param name="Id">The board id.</param>
/// <param name="Name">The board's name.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board's settings (name, description, key, rules or sharing) last changed (UTC); changes to its buckets and cards don't count.</param>
/// <param name="Members">People and agents on the board, owners first.</param>
public record AdminBoard(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<AdminBoardMember> Members);

/// <summary>A member of a board, as listed for admins.</summary>
/// <param name="PrincipalId">The member's principal id: a user id from adminListUsers, or an agent.</param>
/// <param name="Kind">User (a person) or Agent (a machine client).</param>
/// <param name="DisplayName">The name the member chose, or a fallback such as "User 1a2b".</param>
/// <param name="Role">Viewer, Editor or Owner.</param>
/// <param name="AvatarUrl">People only: their chosen picture (see getAvatar); null without one.</param>
public record AdminBoardMember(Guid PrincipalId, PrincipalKind Kind, string DisplayName, BoardRole Role, string? AvatarUrl)
{
    public static AdminBoardMember From(BoardMember member) => new(member.PrincipalId,
        member.Principal is Agent ? PrincipalKind.Agent : PrincipalKind.User, member.Principal!.ShownName, member.Role,
        Avatars.UrlOf(member.Principal));
}

/// <summary>A person with a PrikBoard account, as listed for admins. No email or sign-in details are stored or shown.</summary>
/// <param name="Id">The user's principal id.</param>
/// <param name="DisplayName">The name they chose, or a fallback such as "User 1a2b".</param>
/// <param name="IsAdmin">Whether they are an admin of this PrikBoard.</param>
/// <param name="IsYou">Whether this is the caller.</param>
/// <param name="CreatedAt">When they joined (UTC).</param>
/// <param name="LastSeenAt">When they last signed in (UTC).</param>
/// <param name="BoardCount">How many boards they are a member of.</param>
/// <param name="SoleOwnerOf">Boards they are the only owner of. They cannot be deleted until each has another owner.</param>
/// <param name="AvatarUrl">Their chosen picture (see getAvatar); null without one.</param>
public record AdminUser(
    Guid Id,
    string DisplayName,
    bool IsAdmin,
    bool IsYou,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    int BoardCount,
    IReadOnlyList<AdminBoardRef> SoleOwnerOf,
    string? AvatarUrl);

/// <summary>A board named in an admin answer.</summary>
/// <param name="Id">The board id.</param>
/// <param name="Name">The board's name.</param>
public record AdminBoardRef(Guid Id, string Name);

/// <summary>Gives a person a role on a board, adding them if they are not a member yet.</summary>
/// <param name="Role">Viewer, Editor or Owner. A board always keeps at least one owner.</param>
public record AdminSetBoardMemberRequest(BoardRole Role);

/// <summary>Changes a user's instance-wide rights. A null field stays unchanged.</summary>
/// <param name="IsAdmin">True to make them an admin, false to take it away. There is always at least one admin.</param>
public record AdminUpdateUserRequest(bool? IsAdmin);
