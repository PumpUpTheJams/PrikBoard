using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A personal access token, without the token itself (it is shown once, when created).</summary>
/// <param name="Id">Token id.</param>
/// <param name="Name">What the person called it, for example "Claude Code laptop"; also the agent's name on boards.</param>
/// <param name="Hint">The token's last four characters, to tell tokens apart.</param>
/// <param name="Access">Viewer (read-only) or Editor (can change buckets, notes and images). Capped by your own role on each board.</param>
/// <param name="BoardIds">The only boards the token can use; null for all of your boards.</param>
/// <param name="AgentId">The agent the token acts as; changes made with it show as this agent.</param>
/// <param name="CreatedAt">When it was created (UTC).</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
/// <param name="LastUsedAt">When it was last used, to the minute (UTC); null if never.</param>
public sealed record AccessTokenDetail(
    Guid Id, string Name, string Hint, BoardRole Access, List<Guid>? BoardIds, Guid AgentId,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt)
{
    public static AccessTokenDetail From(AccessToken token, string name) =>
        new(token.Id, name, token.Hint, token.MaxRole, token.BoardIds, token.AgentId, token.CreatedAt, token.ExpiresAt, token.LastUsedAt);
}

/// <summary>A new personal access token.</summary>
/// <param name="Name">What it is for, for example "Claude Code laptop" (shown on boards as the agent's name).</param>
/// <param name="Access">Viewer (read-only) or Editor. Owner is not allowed: tokens never manage boards or members.</param>
/// <param name="BoardIds">Limit the token to these boards (you must be a member); omit for all of your boards, including new ones.</param>
/// <param name="ExpiresInDays">Days until it stops working, 1 to 365; default 90.</param>
public sealed record CreateAccessTokenRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    [property: Required] BoardRole Access,
    List<Guid>? BoardIds = null,
    [property: Range(1, AccessToken.MaxDays)] int? ExpiresInDays = null);

/// <summary>A token that was just created. <see cref="Token"/> is shown only now; store it safely.</summary>
/// <param name="Token">Send it as "Authorization: Bearer &lt;token&gt;" to the API or to /mcp.</param>
/// <param name="Details">The token as it will be listed.</param>
public sealed record CreatedAccessToken(string Token, AccessTokenDetail Details);
