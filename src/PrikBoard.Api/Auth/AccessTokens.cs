using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Personal access tokens: <c>pb_pat_</c>, 43 characters of randomness (32 bytes, base64url) and a 6-character checksum.
/// The prefix lets the API (and secret scanners) recognize them without a database lookup; the checksum rejects typos
/// and garbage before one.
/// </summary>
public static class AccessTokens
{
    public const string Prefix = "pb_pat_";
    private const int RandomChars = 43;
    private const int ChecksumChars = 6;
    private const int Length = 7 + RandomChars + ChecksumChars;

    /// <summary>Owner of a delegated agent (a personal access token): board access follows this person's memberships.</summary>
    public const string OwnerClaim = "prikboard_owner";

    /// <summary>The highest role a delegated agent gets on any board, whatever its owner's role.</summary>
    public const string MaxRoleClaim = "prikboard_max_role";

    /// <summary>Space-separated board ids a delegated agent is limited to; absent for all of the owner's boards.</summary>
    public const string BoardsClaim = "prikboard_boards";

    /// <summary>On <see cref="AccessTokenKind.Discord"/> tokens only: "Discord", which the Discord endpoints require.</summary>
    public const string KindClaim = "prikboard_token_kind";

    /// <summary>Whether the caller is a Discord server's token (<see cref="AccessTokenKind.Discord"/>).</summary>
    public static bool IsDiscordToken(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(KindClaim) == nameof(AccessTokenKind.Discord);

    /// <summary>Whether the caller authenticated with one of PrikBoard's own access tokens (any kind), which <see cref="TokenId"/> names.</summary>
    public static bool IsAccessToken(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ApiScopes.IssuerIdClaim) == Agent.AccessTokenIssuer;

    /// <summary>The id of the personal access token the caller authenticated with.</summary>
    public static Guid TokenId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("client_id")!);

    /// <summary>A new token and the hint shown in lists afterwards.</summary>
    public static (string Token, string Hint) Generate()
    {
        var body = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = Prefix + body + Checksum(body);
        return (token, token[^4..]);
    }

    public static bool LooksLikeOne(string token) => token.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The right shape and checksum, so worth looking up.</summary>
    public static bool IsWellFormed(string token)
    {
        if (token.Length != Length || !LooksLikeOne(token)) return false;
        var body = token.AsSpan(Prefix.Length, RandomChars);
        return token.AsSpan(Prefix.Length + RandomChars).SequenceEqual(Checksum(body.ToString()));
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Checksum(string body) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..ChecksumChars];

    /// <summary>The scopes that go with a token's maximum role: Viewer is read-only.</summary>
    public static string ScopesFor(BoardRole maxRole) =>
        maxRole >= BoardRole.Editor ? $"{ApiScopes.Read} {ApiScopes.Write}" : ApiScopes.Read;
}

/// <summary>
/// Who a caller is on boards. People and issuer agents are members themselves. A delegated agent (personal access token)
/// uses its owner's memberships, capped at the token's maximum role and limited to its boards.
/// </summary>
/// <param name="MemberId">The principal whose board memberships count.</param>
/// <param name="Cap">The highest role the caller can have.</param>
/// <param name="Boards">The only boards the caller may use, or null for all.</param>
/// <param name="Delegated">A personal access token acting for <paramref name="MemberId"/>.</param>
public sealed record BoardCaller(Guid MemberId, BoardRole Cap, IReadOnlySet<Guid>? Boards, bool Delegated)
{
    public static BoardCaller Of(ClaimsPrincipal principal)
    {
        if (principal.FindFirstValue(AccessTokens.OwnerClaim) is not { } owner)
        {
            return new BoardCaller(principal.PrincipalId(), BoardRole.Owner, null, false);
        }

        var cap = Enum.Parse<BoardRole>(principal.FindFirstValue(AccessTokens.MaxRoleClaim)!);
        var boards = principal.FindFirstValue(AccessTokens.BoardsClaim)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToHashSet();
        return new BoardCaller(Guid.Parse(owner), cap, boards, true);
    }

    /// <summary>The caller's role on a board, given the member's role there (null when not a member).</summary>
    public BoardRole? Apply(Guid boardId, BoardRole? memberRole) =>
        memberRole is not { } role || (Boards is not null && !Boards.Contains(boardId)) ? null : role < Cap ? role : Cap;
}
