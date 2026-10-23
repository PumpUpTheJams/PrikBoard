using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Someone or something that can be a board member: a <see cref="User"/> (a person who signs in) or an <see cref="Agent"/>
/// (a machine client with access tokens). Identified only by a GUID; no personal data is stored.
/// </summary>
public abstract class Principal
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The name this principal chose to show to others; null until they pick one.</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The board this principal last used through the MCP tools, which they are told about when they connect; null before the first.</summary>
    public Guid? LastBoardId { get; set; }

    /// <summary>What others see when no display name was chosen.</summary>
    public abstract string FallbackName { get; }

    public string ShownName => DisplayName ?? FallbackName;
}

/// <summary>
/// A person. They sign in through one or more linked <see cref="Identities"/> (GitHub, Google, ...), which hold only
/// keyed hashes, never the provider's ids, names or emails.
/// </summary>
public class User : Principal
{
    public List<UserIdentity> Identities { get; set; } = [];

    /// <summary>
    /// Runs the whole instance: sees every board and user, sets anyone's board roles, and deletes boards and users. The
    /// first user becomes one; there is always at least one.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// When the copy of their sign-in provider's picture (<see cref="AvatarFile"/>) last changed; null without one.
    /// Only the copy is kept, never the picture's URL, which holds the provider's account id.
    /// </summary>
    public DateTimeOffset? AvatarUpdatedAt { get; set; }

    /// <summary>
    /// The person removed their avatar, so sign-ins no longer take the provider's picture. Cleared when they take it again.
    /// </summary>
    public bool AvatarOptedOut { get; set; }

    /// <summary>The stored copy of their avatar picture; null without one.</summary>
    public Guid? AvatarFileId { get; set; }
    public StoredFile? AvatarFile { get; set; }

    // GUID v7 starts with a timestamp, so the random tail tells people apart better than the head.
    public override string FallbackName => $"User {Id.ToString("N")[^4..]}";
}

/// <summary>
/// A machine client (for example an AI agent): a client of a trusted token issuer, which owners add to boards; the agent
/// behind a person's access token (<see cref="OwnerId"/> set), which works on its owner's boards; or a connected Discord
/// server (<see cref="DiscordGuild"/>).
/// </summary>
public class Agent : Principal
{
    /// <summary>Issuer id for personal access tokens.</summary>
    public const string AccessTokenIssuer = "pat";

    /// <summary>Issuer id of Discord servers connected for link previews (<see cref="DiscordGuild"/>); the client id is the guild id.</summary>
    public const string DiscordIssuer = "discord";

    /// <summary>Id of the configured token issuer (<c>Auth:Tokens:Issuers</c>), <see cref="AccessTokenIssuer"/> or <see cref="DiscordIssuer"/>.</summary>
    public required string Issuer { get; set; }

    /// <summary>The client id in its tokens (azp, client_id or appid), the access token's id, or the Discord guild id: never personal data.</summary>
    public required string ClientId { get; set; }

    /// <summary>The person whose access token this agent acts for; null for issuer clients and Discord servers.</summary>
    public Guid? OwnerId { get; set; }

    public override string FallbackName => $"Agent {(ClientId.Length <= 60 ? ClientId : ClientId[..60])}";
}

/// <summary>One sign-in a user linked: the provider and keyed hashes of its subject id and (verified) email.</summary>
public class UserIdentity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Id of the configured sign-in provider, for example "github".</summary>
    public required string Provider { get; set; }

    /// <summary>HMAC of the provider's subject id; see <c>KeyedHasher.Subject</c>.</summary>
    public required byte[] SubjectHash { get; set; }

    /// <summary>HMAC of the verified email from the last sign-in through this identity, for exact-email search; null without one.</summary>
    public byte[]? EmailHash { get; set; }

    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What a board member may do. Ordered: each role includes the ones below it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardRole>))]
public enum BoardRole
{
    /// <summary>Reads the board.</summary>
    Viewer = 1,

    /// <summary>Also changes the board's content: buckets, cards, images, drawings and tags.</summary>
    Editor = 2,

    /// <summary>Also renames or deletes the board, sets its key, rules and public settings, and manages its members and invites.</summary>
    Owner = 3,
}

public class BoardMember
{
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public Guid PrincipalId { get; set; }
    public Principal? Principal { get; set; }
    public BoardRole Role { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A single-use link that makes whoever accepts it a member with <see cref="Role"/>. Only a hash of the token is kept.</summary>
public class BoardInvite
{
    /// <summary>The longest invite token there is (the limit on the token in invite requests too).</summary>
    public const int MaxTokenLength = 100;
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public BoardRole Role { get; set; }
    public required byte[] TokenHash { get; set; }
    public Guid? CreatedById { get; set; }
    public Principal? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? AcceptedById { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }

    public const int DefaultDays = 7;
    public const int MaxDays = 30;
}
