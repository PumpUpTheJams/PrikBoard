using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Integrations;

namespace PrikBoard.Api.Contracts;

/// <summary>A Discord channel.</summary>
/// <param name="Id">Discord's id for it.</param>
/// <param name="Name">Its name, without the #.</param>
public sealed record DiscordChannelDetail(string Id, string Name)
{
    public static List<DiscordChannelDetail>? From(IEnumerable<DiscordChannel>? channels) =>
        channels?.Select(c => new DiscordChannelDetail(c.Id, c.Name)).ToList();
}

/// <summary>A board a Discord server is connected to.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="ShortId">Its short id, as in its links.</param>
/// <param name="BoardName">Its name.</param>
/// <param name="Channels">The channels its links are previewed in, or null for the whole server.</param>
public sealed record DiscordBoardConnection(Guid BoardId, string ShortId, string BoardName, List<DiscordChannelDetail>? Channels);

/// <summary>A Discord server connected to a board, for its owners.</summary>
/// <param name="GuildId">Discord's id for the server.</param>
/// <param name="GuildName">Its name.</param>
/// <param name="AgentId">The server's member on the board (a Viewer agent).</param>
/// <param name="Channels">The channels the board's links are previewed in, or null for the whole server.</param>
/// <param name="ConnectedAt">When it was connected to this board (UTC).</param>
/// <param name="ConnectedBy">Who made the server's latest connection, if they still exist.</param>
public sealed record DiscordConnection(
    string GuildId, string GuildName, Guid AgentId, List<DiscordChannelDetail>? Channels, DateTimeOffset ConnectedAt, string? ConnectedBy);

/// <summary>A Discord server the bot is in, with the channels a board can be previewed in.</summary>
/// <param name="GuildId">Discord's id for the server.</param>
/// <param name="Name">Its name.</param>
/// <param name="Channels">The text channels where the bot may read and post, in Discord's order.</param>
public sealed record DiscordServerChoice(string GuildId, string Name, List<DiscordChannelDetail> Channels);

/// <summary>Where the instance's Discord bot can preview a board's links.</summary>
/// <param name="State">Off (no admin set up a bot), Connecting, Online or Failed.</param>
/// <param name="Servers">The servers the bot is in; empty while it is not online.</param>
public sealed record DiscordServers(DiscordBotState State, List<DiscordServerChoice> Servers);

/// <summary>Previews a board's links in a Discord channel, or in the whole server.</summary>
/// <param name="GuildId">The server, from listDiscordServers.</param>
/// <param name="ChannelId">A channel of it, from listDiscordServers; null for every channel. A channel added to a server connected as a whole limits it to that channel.</param>
public sealed record ConnectDiscordRequest(
    [property: Required, StringLength(DiscordGuild.IdMaxLength, MinimumLength = 1)] string GuildId,
    [property: StringLength(DiscordGuild.IdMaxLength, MinimumLength = 1)] string? ChannelId = null);

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
