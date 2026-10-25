using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Integrations;

namespace PrikBoard.Api.Contracts;

/// <summary>Board activity a Discord channel can follow.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscordUpdate>))]
public enum DiscordUpdate
{
    /// <summary>A card was added (created, pasted, restored, or moved here from another board).</summary>
    NewCards,

    /// <summary>A card moved from one bucket to another.</summary>
    Moves,

    /// <summary>Someone was assigned to a card.</summary>
    Assignments,

    /// <summary>An agent started or stopped working on a card.</summary>
    AgentWork,
}

/// <summary>Converts between the API's list of updates and the stored flags.</summary>
public static class DiscordUpdateLists
{
    private static readonly (DiscordUpdate Update, ChatUpdates Flag)[] Pairs =
    [
        (DiscordUpdate.NewCards, ChatUpdates.NewCards),
        (DiscordUpdate.Moves, ChatUpdates.Moves),
        (DiscordUpdate.Assignments, ChatUpdates.Assignments),
        (DiscordUpdate.AgentWork, ChatUpdates.AgentWork),
    ];

    public static List<DiscordUpdate> From(ChatUpdates flags) => Pairs.Where(p => flags.HasFlag(p.Flag)).Select(p => p.Update).ToList();

    public static ChatUpdates Flags(IEnumerable<DiscordUpdate> updates) =>
        updates.Aggregate(ChatUpdates.None, (all, u) => all | Pairs.First(p => p.Update == u).Flag);
}

/// <summary>A Discord channel connected to a board, with what it does there.</summary>
/// <param name="Id">Discord's id for it.</param>
/// <param name="Name">Its name, without the #.</param>
/// <param name="Updates">What the bot posts there as it happens on the board.</param>
/// <param name="SaveBucketId">Where Save to PrikBoard in this channel puts cards; null for the board's first bucket.</param>
public sealed record DiscordChannelDetail(string Id, string Name, List<DiscordUpdate> Updates, Guid? SaveBucketId)
{
    public static List<DiscordChannelDetail> From(IEnumerable<ChatChannel> channels) =>
        channels.Select(c => new DiscordChannelDetail(c.Id, c.Name, DiscordUpdateLists.From(c.Updates), c.SaveBucketId)).ToList();
}

/// <summary>A board a Discord server is connected to.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="ShortId">Its short id, as in its links.</param>
/// <param name="BoardName">Its name.</param>
/// <param name="WholeServer">Whether its links preview in every channel of the server.</param>
/// <param name="Channels">The channels picked for it.</param>
public sealed record DiscordBoardConnection(Guid BoardId, string ShortId, string BoardName, bool WholeServer, List<DiscordChannelDetail> Channels);

/// <summary>A Discord server connected to a board, for its owners.</summary>
/// <param name="GuildId">Discord's id for the server.</param>
/// <param name="GuildName">Its name.</param>
/// <param name="AgentId">The server's member on the board (a Viewer agent).</param>
/// <param name="WholeServer">Whether the board's links preview in every channel of the server.</param>
/// <param name="Channels">The channels picked for the board: its links preview there, and each can post updates and take Save to PrikBoard.</param>
/// <param name="ConnectedAt">When it was connected to this board (UTC).</param>
/// <param name="ConnectedBy">Who made the server's latest connection, if they still exist.</param>
public sealed record DiscordConnection(
    string GuildId, string GuildName, Guid AgentId, bool WholeServer, List<DiscordChannelDetail> Channels, DateTimeOffset ConnectedAt, string? ConnectedBy);

/// <summary>A text channel the bot may read and post in.</summary>
/// <param name="Id">Discord's id for it.</param>
/// <param name="Name">Its name, without the #.</param>
public sealed record DiscordChannelChoice(string Id, string Name);

/// <summary>A Discord server the bot is in, with the channels a board can be previewed in.</summary>
/// <param name="GuildId">Discord's id for the server.</param>
/// <param name="Name">Its name.</param>
/// <param name="Channels">The text channels where the bot may read and post, in Discord's order.</param>
public sealed record DiscordServerChoice(string GuildId, string Name, List<DiscordChannelChoice> Channels);

/// <summary>Where the instance's Discord bot can preview a board's links.</summary>
/// <param name="State">Off (no admin set up a bot), Connecting, Online or Failed.</param>
/// <param name="Servers">The servers the bot is in; empty while it is not online.</param>
public sealed record DiscordServers(DiscordBotState State, List<DiscordServerChoice> Servers);

/// <summary>Connects a board to a Discord channel, or to the whole server.</summary>
/// <param name="GuildId">The server, from listDiscordServers.</param>
/// <param name="ChannelId">A channel of it, from listDiscordServers; null for every channel. Channels and the whole server can be combined.</param>
public sealed record ConnectDiscordRequest(
    [property: Required, StringLength(DiscordIds.MaxLength, MinimumLength = 1)] string GuildId,
    [property: StringLength(DiscordIds.MaxLength, MinimumLength = 1)] string? ChannelId = null);

/// <summary>What a channel connected to a board does for it. Both fields replace what was set.</summary>
/// <param name="Updates">The board activity the bot posts in the channel; empty for none.</param>
/// <param name="SaveBucketId">The bucket Save to PrikBoard in this channel puts cards in; null for the board's first bucket.</param>
public sealed record UpdateDiscordChannelRequest(
    [property: Required] List<DiscordUpdate> Updates,
    Guid? SaveBucketId = null);

/// <summary>Where your Discord account is linked from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscordAccountSource>))]
public enum DiscordAccountSource
{
    /// <summary>You sign in to PrikBoard with Discord, which says who you are.</summary>
    SignIn,

    /// <summary>You linked it from the bot's "Connect your PrikBoard account" button.</summary>
    Link,
}

/// <summary>Whether the Discord bot knows who you are in Discord, for Save to PrikBoard.</summary>
/// <param name="Linked">Whether it does.</param>
/// <param name="Source">How: a Discord sign-in, or a link you made; null when not linked.</param>
/// <param name="LinkedAt">When you made the link (Link only).</param>
public sealed record DiscordAccountStatus(bool Linked, DiscordAccountSource? Source, DateTimeOffset? LinkedAt);

/// <summary>The Discord user behind a link code, so you can check it is you before linking.</summary>
/// <param name="DiscordName">Their name in Discord, as Discord gave it to the bot.</param>
/// <param name="ExpiresAt">When the code stops working (UTC).</param>
public sealed record DiscordAccountLinkPreview(string DiscordName, DateTimeOffset ExpiresAt);

/// <summary>Links the Discord account behind a code from the bot to you.</summary>
/// <param name="Code">The code from the bot's "Connect your PrikBoard account" link.</param>
public sealed record LinkDiscordAccountRequest([property: Required, StringLength(100, MinimumLength = 1)] string Code);

/// <summary>Where the Discord bot's token comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscordBotSource>))]
public enum DiscordBotSource
{
    /// <summary>Discord:BotToken in the server's configuration: read-only here.</summary>
    Configuration,

    /// <summary>Saved on the admin page.</summary>
    Setup,
}

/// <summary>The instance's Discord bot, for admins. The token is never returned.</summary>
/// <param name="State">Off, Connecting, Online or Failed.</param>
/// <param name="Problem">Why it failed, when it did.</param>
/// <param name="Source">Where the token comes from; null without one.</param>
/// <param name="BotName">The bot's name in Discord.</param>
/// <param name="ApplicationId">Its Discord application.</param>
/// <param name="InviteUrl">The link that adds the bot to a server, with the permissions it needs.</param>
/// <param name="IsPublic">Whether anyone may add the bot to their server ("Public Bot" in Discord), once it is online. Turn it off so only you can.</param>
/// <param name="PublicUrl">The PrikBoard address pasted links are matched against.</param>
/// <param name="PublicUrlIsConfigured">Whether that comes from Discord:PublicUrl in the server's configuration (read-only here).</param>
/// <param name="UpdatedAt">When an admin last saved the settings.</param>
/// <param name="Servers">The servers the bot is in.</param>
public sealed record DiscordBotDetail(
    DiscordBotState State,
    string? Problem,
    DiscordBotSource? Source,
    string? BotName,
    string? ApplicationId,
    string? InviteUrl,
    bool? IsPublic,
    string? PublicUrl,
    bool PublicUrlIsConfigured,
    DateTimeOffset? UpdatedAt,
    List<DiscordBotServer> Servers);

/// <summary>A server the bot is in, for admins.</summary>
/// <param name="GuildId">Discord's id for the server.</param>
/// <param name="Name">Its name.</param>
/// <param name="ChannelCount">Channels where the bot may read and post.</param>
/// <param name="Boards">The boards previewed there.</param>
public sealed record DiscordBotServer(string GuildId, string Name, int ChannelCount, List<DiscordBoardConnection> Boards);

/// <summary>Sets up the instance's Discord bot. Fields left out stay as they are.</summary>
/// <param name="BotToken">The token from the Bot tab of a Discord application, whose Message Content intent is on. Checked with Discord, then stored encrypted.</param>
/// <param name="PublicUrl">The PrikBoard address people paste links from, such as https://board.example.com. Defaults to the address this request came to.</param>
public sealed record SetDiscordBotRequest(
    [property: StringLength(200, MinimumLength = 1)] string? BotToken = null,
    [property: StringLength(DiscordBotSettings.PublicUrlMaxLength, MinimumLength = 1)] string? PublicUrl = null);
