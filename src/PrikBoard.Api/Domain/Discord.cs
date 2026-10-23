namespace PrikBoard.Api.Domain;

/// <summary>
/// A Discord server (a guild in Discord's API) connected to PrikBoard, so the instance's Discord bot can preview links
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

    /// <summary>The server's agent; its display name is the server's name when a board was last connected to it.</summary>
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }

    /// <summary>The board owner who made the latest connection; null once they no longer exist.</summary>
    public Guid? ConnectedById { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A Discord id as Discord writes it: 1 to 20 digits.</summary>
    public static bool IsId(string? value) => value is { Length: > 0 and <= IdMaxLength } && !value.AsSpan().ContainsAnyExceptInRange('0', '9');
}

/// <summary>
/// Where a connected server may preview one board: the channels its owners picked, or the whole server. The guild's
/// membership of the board is the grant; this row only narrows it.
/// </summary>
public class DiscordBoardChannels
{
    /// <summary>Channels one board can be limited to in a server; beyond that, connect it to the whole server.</summary>
    public const int MaxChannels = 50;

    public required string GuildId { get; set; }
    public Guid BoardId { get; set; }

    /// <summary>The channels previews of this board are limited to, named as they were when picked; null for the whole server.</summary>
    public List<DiscordChannel>? Channels { get; set; }

    /// <summary>Whether a link to the board may be previewed in this channel.</summary>
    public bool Allows(string channelId) => Channels is null || Channels.Any(c => c.Id == channelId);
}

/// <summary>A Discord channel: its id and its name when PrikBoard last heard it.</summary>
public sealed record DiscordChannel(string Id, string Name);

/// <summary>
/// The instance's Discord bot, set up by an admin in Admin → Instance (docs/discord.md). One row at most;
/// <c>Discord:BotToken</c> and <c>Discord:PublicUrl</c> in configuration win over it. <see cref="Integrations.DiscordBot"/>
/// reads it at startup and after every change, and connects the bot while the app runs.
/// </summary>
public class DiscordBotSettings
{
    /// <summary>The only row's id.</summary>
    public const int SingleId = 1;

    public const int PublicUrlMaxLength = 500;

    public int Id { get; set; } = SingleId;

    /// <summary>The bot's token, encrypted with Data Protection (<see cref="Integrations.DiscordBot.TokenPurpose"/>).</summary>
    public string? ProtectedBotToken { get; set; }

    /// <summary>The Discord application the token belongs to, as Discord said when it was saved; for the invite link.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>The bot's name in Discord when the token was saved.</summary>
    public string? BotName { get; set; }

    /// <summary>The PrikBoard address people paste links from, e.g. https://board.example.com.</summary>
    public string? PublicUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The admin who changed it last; null once they no longer exist.</summary>
    public Guid? UpdatedById { get; set; }
}
