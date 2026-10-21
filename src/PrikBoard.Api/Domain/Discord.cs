namespace PrikBoard.Api.Domain;

/// <summary>
/// A Discord server (a guild in Discord's API) connected to PrikBoard, so the PrikBoard Discord bot can preview links
/// to boards there. The server is an <see cref="Agent"/> (issuer <see cref="Agent.DiscordIssuer"/>, client id the guild
/// id) that is a Viewer member of each connected board: that membership is the grant, and removing it disconnects the
/// board. This row keeps what the agent can't hold. See docs/discord.md.
/// </summary>
public class DiscordGuild
{
    /// <summary>The longest Discord id (a snowflake, a 64-bit number written in decimal).</summary>
    public const int IdMaxLength = 20;

    /// <summary>Discord's id for the server, kept as text. Not personal data about PrikBoard users.</summary>
    public required string GuildId { get; set; }

    /// <summary>The server's agent; its display name is the server's name as Discord gave it at the latest connect.</summary>
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }

    /// <summary>The PrikBoard user who confirmed the latest connection; null once they no longer exist.</summary>
    public Guid? ConnectedById { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A Discord id as Discord writes it: 1 to 20 digits.</summary>
    public static bool IsId(string? value) => value is { Length: > 0 and <= IdMaxLength } && value.All(char.IsAsciiDigit);
}

/// <summary>
/// Where a connected server may preview one board: the channels picked when it was connected, or the whole server. The
/// guild's membership of the board is the grant; this row only narrows it.
/// </summary>
public class DiscordBoardChannels
{
    public required string GuildId { get; set; }
    public Guid BoardId { get; set; }

    /// <summary>The channels previews of this board are limited to, as Discord named them at connect time; null for the whole server.</summary>
    public List<DiscordChannel>? Channels { get; set; }

    /// <summary>Whether a link to the board may be previewed in this channel.</summary>
    public bool Allows(string channelId) => Channels is null || Channels.Any(c => c.Id == channelId);
}

/// <summary>A Discord channel: its id and its name when PrikBoard last heard it.</summary>
public sealed record DiscordChannel(string Id, string Name);

/// <summary>
/// A board owner approved connecting a Discord server: the one-time code the bot trades, with its PKCE verifier and the
/// server and channel Discord vouches for, for a <see cref="AccessTokenKind.Discord"/> token. Only a hash of the code is
/// stored; it is deleted when used.
/// </summary>
public class DiscordConnectGrant
{
    /// <summary>How long the Discord admin has to run /prikboard finish.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Codes waiting to be traded per person, so a session cannot pile them up.</summary>
    public const int MaxPendingPerOwner = 5;

    /// <summary>Boards one connect can take.</summary>
    public const int MaxBoards = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>SHA-256 of the code (256 random bits).</summary>
    public required byte[] CodeHash { get; set; }

    /// <summary>The bot's PKCE code challenge: base64url SHA-256 of its verifier (S256).</summary>
    public required string CodeChallenge { get; set; }

    /// <summary>The boards to connect, limited to the channel where /prikboard connect was run.</summary>
    public List<Guid> ChannelBoardIds { get; set; } = [];

    /// <summary>The boards to connect for the whole server.</summary>
    public List<Guid> WholeServerBoardIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
