using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A personal access token, without the token itself (it is shown once, when created).</summary>
/// <param name="Id">Token id.</param>
/// <param name="Kind">Agent: acts as its own agent with capped access. App: a PrikBoard app (the VS Code extension) signed in as you.</param>
/// <param name="Name">What the person called it, for example "Claude Code laptop"; also the agent's name on boards.</param>
/// <param name="Hint">The token's last four characters, to tell tokens apart.</param>
/// <param name="Access">Viewer (read-only) or Editor (can change buckets, cards and images). Capped by your own role on each board. App tokens have your own roles.</param>
/// <param name="BoardIds">The only boards the token can use; null for all of your boards.</param>
/// <param name="AgentId">The agent the token acts as; changes made with it show as this agent. Null for app tokens, which act as you.</param>
/// <param name="CreatedAt">When it was created (UTC).</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
/// <param name="LastUsedAt">When it was last used, to the minute (UTC); null if never.</param>
public sealed record AccessTokenDetail(
    Guid Id, AccessTokenKind Kind, string Name, string Hint, BoardRole Access, List<Guid>? BoardIds, Guid? AgentId,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt)
{
    public static AccessTokenDetail From(AccessToken token, string name) =>
        new(token.Id, token.Kind, name, token.Hint, token.MaxRole, token.BoardIds, token.AgentId, token.CreatedAt, token.ExpiresAt, token.LastUsedAt);
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

/// <summary>Approves an app's sign-in (the VS Code extension), from the PrikBoard page the app opened.</summary>
/// <param name="Name">What the app calls itself, for example "VS Code on laptop"; the token gets this name.</param>
/// <param name="CodeChallenge">The app's PKCE code challenge: base64url SHA-256 of its code verifier (S256), 43 characters.</param>
/// <param name="RedirectUri">Where the app listens for the code: a loopback address (http://127.0.0.1, localhost or [::1] with a port). Omit when you will paste the code into the app.</param>
public sealed record ApproveAppSignInRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    [property: Required, StringLength(43, MinimumLength = 43)] string CodeChallenge,
    [property: StringLength(200)] string? RedirectUri = null);

/// <summary>The one-time code for the app. It works for 5 minutes, once, and only with the app's code verifier.</summary>
/// <param name="Code">Send it to the app's redirect address, or paste it into the app.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
public sealed record AppSignInCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>An app trades its one-time code for a token.</summary>
/// <param name="Code">The code from approveAppSignIn.</param>
/// <param name="CodeVerifier">The PKCE code verifier whose S256 challenge the app sent along (43 to 128 characters).</param>
public sealed record ExchangeAppSignInRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Code,
    [property: Required, StringLength(128, MinimumLength = 43)] string CodeVerifier);
