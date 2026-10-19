using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A member of a board: a person or an agent, with their role.</summary>
/// <param name="PrincipalId">The member's principal id.</param>
/// <param name="Kind">User (a person) or Agent (a machine client).</param>
/// <param name="DisplayName">The name the member chose, or a fallback such as "User 1a2b".</param>
/// <param name="Role">Viewer, Editor or Owner.</param>
/// <param name="AddedAt">When they joined the board (UTC).</param>
/// <param name="IsYou">Whether this member is the caller.</param>
/// <param name="Issuer">Agents only: the token issuer id.</param>
/// <param name="ClientId">Agents only: the client id.</param>
/// <param name="AvatarUrl">People only: their chosen picture (see getAvatar); null without one.</param>
public record BoardMemberDetail(
    Guid PrincipalId,
    PrincipalKind Kind,
    string DisplayName,
    BoardRole Role,
    DateTimeOffset AddedAt,
    bool IsYou,
    string? Issuer,
    string? ClientId,
    string? AvatarUrl)
{
    public static BoardMemberDetail From(BoardMember member, Guid caller) => member.Principal switch
    {
        Agent agent => new(member.PrincipalId, PrincipalKind.Agent, agent.ShownName, member.Role, member.AddedAt,
            member.PrincipalId == caller, agent.Issuer, agent.ClientId, null),
        var principal => new(member.PrincipalId, PrincipalKind.User, principal!.ShownName, member.Role, member.AddedAt,
            member.PrincipalId == caller, null, null, Avatars.UrlOf(principal)),
    };
}

/// <summary>Adds a person found with findUserByEmail (or any known principal) to the board.</summary>
/// <param name="PrincipalId">The principal to add.</param>
/// <param name="Role">Their role.</param>
public record AddBoardMemberRequest(Guid PrincipalId, BoardRole Role);

/// <summary>Adds a machine client to the board. It does not have to have called the API yet.</summary>
/// <param name="Issuer">Id of a configured token issuer (see listTokenIssuers).</param>
/// <param name="ClientId">The client id in its tokens (azp, client_id or appid).</param>
/// <param name="Role">Its role.</param>
/// <param name="DisplayName">A name for the agent, used only if it has none yet.</param>
public record AddBoardAgentRequest(
    [property: Required, StringLength(50)] string Issuer,
    [property: Required, StringLength(255, MinimumLength = 1)] string ClientId,
    BoardRole Role,
    [property: StringLength(100)] string? DisplayName = null);

/// <summary>Changes a member's role. A board always keeps at least one owner.</summary>
/// <param name="Role">The new role.</param>
public record UpdateBoardMemberRequest(BoardRole Role);

/// <summary>Looks a person up by their exact email address.</summary>
/// <param name="Email">The full address; partial addresses match nothing.</param>
public record FindUserRequest([property: Required, EmailAddress, StringLength(320)] string Email);

/// <summary>A person whose verified email matched.</summary>
/// <param name="PrincipalId">Use this with addBoardMember.</param>
/// <param name="DisplayName">The name they chose, or a fallback.</param>
/// <param name="Role">Their role on this board, or null when they are not a member yet.</param>
public record UserMatch(Guid PrincipalId, string DisplayName, BoardRole? Role);
