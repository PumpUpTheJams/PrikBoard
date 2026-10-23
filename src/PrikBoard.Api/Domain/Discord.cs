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
/// Where a connected server may preview one board, and what each of those channels does for it: the channels its owners
/// picked, the whole server, or both (a channel then only adds its own settings, such as posting updates). The guild's
/// membership of the board is the grant; this row only narrows it.
/// </summary>
public class DiscordBoardChannels
{
    /// <summary>Channels one board can be connected to in a server; beyond that, connect it to the whole server.</summary>
    public const int MaxChannels = 50;

    public required string GuildId { get; set; }
    public Guid BoardId { get; set; }

    /// <summary>Whether links to the board preview in every channel of the server.</summary>
    public bool WholeServer { get; set; }

    /// <summary>The channels picked for this board, with their settings, named as they were when last seen.</summary>
    public List<DiscordChannel> Channels { get; set; } = [];

    /// <summary>Whether a link to the board may be previewed in this channel.</summary>
    public bool Allows(string channelId) => WholeServer || Channels.Any(c => c.Id == channelId);

    /// <summary>The settings of a channel picked for this board; null for one that isn't (also under the whole server).</summary>
    public DiscordChannel? Channel(string channelId) => Channels.FirstOrDefault(c => c.Id == channelId);
}

/// <summary>A Discord channel connected to a board: its id, its name when PrikBoard last heard it, and what it does there.</summary>
/// <param name="Id">Discord's id for the channel.</param>
/// <param name="Name">Its name, without the #.</param>
/// <param name="Updates">What the bot posts in the channel as it happens on the board.</param>
/// <param name="SaveBucketId">Where "Save to PrikBoard" in this channel puts cards; null for the board's first bucket.</param>
public sealed record DiscordChannel(string Id, string Name, DiscordUpdates Updates = DiscordUpdates.None, Guid? SaveBucketId = null);

/// <summary>The board activity a channel can follow. Stored as a number, so the names may change but never the values.</summary>
[Flags]
public enum DiscordUpdates
{
    None = 0,

    /// <summary>A card was added (created, pasted, restored or moved here from another board).</summary>
    NewCards = 1,

    /// <summary>A card moved from one bucket to another.</summary>
    Moves = 2,

    /// <summary>Someone was assigned to a card.</summary>
    Assignments = 4,

    /// <summary>An agent started or stopped working on a card.</summary>
    AgentWork = 8,
}

/// <summary>
/// A Discord user linked to a PrikBoard user, so what they do through the bot (Save to PrikBoard) is done as that person,
/// with their own role on the board. Only a keyed hash of the Discord user id is stored (<see cref="Auth.KeyedHasher"/>),
/// like sign-in subjects. People who sign in with a Discord provider need no link: their sign-in already says who they are.
/// </summary>
public class DiscordAccount
{
    /// <summary>HMAC of the Discord user id (<c>KeyedHasher.Subject(DiscordAccounts.HashProvider, id)</c>).</summary>
    public required byte[] DiscordUserHash { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A reply with link previews the bot posted, remembered for <see cref="KeptFor"/> so it can edit the embeds when the
/// cards change, or say a link is no longer available once its board is disconnected.
/// </summary>
public class DiscordPreviewMessage
{
    /// <summary>How long posted previews follow their cards.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>The bot's reply (Discord's message id).</summary>
    public required string MessageId { get; set; }

    public required string GuildId { get; set; }
    public required string ChannelId { get; set; }

    /// <summary>What was previewed, in the reply's order: pasted links, or bare readable ids (PRIK-149).</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>The cards and boards the reply shows, to find it when one of them changes.</summary>
    public List<Guid> CardIds { get; set; } = [];
    public List<Guid> BoardIds { get; set; } = [];

    public DateTimeOffset PostedAt { get; set; } = DateTimeOffset.UtcNow;
}

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
