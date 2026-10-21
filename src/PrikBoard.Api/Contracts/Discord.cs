using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A board to connect to a Discord server, and where in the server its links may be previewed.</summary>
/// <param name="BoardId">The board; you must be its owner.</param>
/// <param name="WholeServer">True for every channel of the server; false (the default) for only the channel where /prikboard connect was run.</param>
public sealed record DiscordBoardChoice(Guid BoardId, bool WholeServer = false);

/// <summary>You approve connecting a Discord server to boards you own, from the page the bot linked to.</summary>
/// <param name="CodeChallenge">The bot's PKCE code challenge from the link: base64url SHA-256 of its code verifier (S256), 43 characters.</param>
/// <param name="Boards">The boards to connect (1 to 50), each limited to the channel or for the whole server.</param>
public sealed record ApproveDiscordConnectRequest(
    [property: Required, StringLength(43, MinimumLength = 43)] string CodeChallenge,
    [property: Required, MinLength(1), MaxLength(DiscordConnectGrant.MaxBoards)] List<DiscordBoardChoice> Boards);

/// <summary>The one-time code to paste into Discord with /prikboard finish. It works for 5 minutes, once, and only in the server that started connecting.</summary>
/// <param name="Code">The code.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
public sealed record DiscordConnectCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// The bot trades a code for the server's token. The server and channel come from Discord's interaction, which Discord
/// vouches for, not from anything a person typed.
/// </summary>
/// <param name="Code">The code from approveDiscordConnect, as pasted in /prikboard finish.</param>
/// <param name="CodeVerifier">The PKCE code verifier the bot kept for this server since /prikboard connect (43 to 128 characters).</param>
/// <param name="GuildId">The Discord server's id.</param>
/// <param name="GuildName">The server's name, shown on the boards' member lists.</param>
/// <param name="ChannelId">The channel where /prikboard connect was run.</param>
/// <param name="ChannelName">That channel's name.</param>
public sealed record ExchangeDiscordConnectRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Code,
    [property: Required, StringLength(128, MinimumLength = 43)] string CodeVerifier,
    [property: Required, StringLength(DiscordGuild.IdMaxLength, MinimumLength = 1)] string GuildId,
    [property: Required, StringLength(100, MinimumLength = 1)] string GuildName,
    [property: Required, StringLength(DiscordGuild.IdMaxLength, MinimumLength = 1)] string ChannelId,
    [property: Required, StringLength(100, MinimumLength = 1)] string ChannelName);

/// <summary>A Discord channel.</summary>
/// <param name="Id">Discord's id for it.</param>
/// <param name="Name">Its name when the server was connected from it.</param>
public sealed record DiscordChannelDetail(string Id, string Name)
{
    public static List<DiscordChannelDetail>? From(List<DiscordChannel>? channels) =>
        channels?.Select(c => new DiscordChannelDetail(c.Id, c.Name)).ToList();
}

/// <summary>A board a Discord server is connected to.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="ShortId">Its short id, as in its links.</param>
/// <param name="BoardName">Its name.</param>
/// <param name="Channels">The channels its links are previewed in, or null for the whole server.</param>
public sealed record DiscordBoardConnection(Guid BoardId, string ShortId, string BoardName, List<DiscordChannelDetail>? Channels);

/// <summary>A Discord server's connection, for the bot.</summary>
/// <param name="Token">The server's new token (shown only now): send it as "Authorization: Bearer &lt;token&gt;". It replaces the server's old token.</param>
/// <param name="ExpiresAt">When the token stops working (UTC); connecting again renews it.</param>
/// <param name="Boards">Every board the server is connected to now.</param>
public sealed record DiscordConnected(string Token, DateTimeOffset ExpiresAt, List<DiscordBoardConnection> Boards);

/// <summary>What a connected Discord server can preview.</summary>
/// <param name="GuildId">The server's id.</param>
/// <param name="GuildName">Its name as PrikBoard knows it.</param>
/// <param name="TokenExpiresAt">When this token stops working (UTC).</param>
/// <param name="Boards">The boards it is connected to, by name.</param>
public sealed record DiscordStatus(string GuildId, string GuildName, DateTimeOffset TokenExpiresAt, List<DiscordBoardConnection> Boards);

/// <summary>A Discord server connected to a board, for its owners.</summary>
/// <param name="GuildId">Discord's id for the server.</param>
/// <param name="GuildName">Its name when it was last connected.</param>
/// <param name="AgentId">The server's member on the board (a Viewer agent).</param>
/// <param name="Channels">The channels the board's links are previewed in, or null for the whole server.</param>
/// <param name="ConnectedAt">When it was connected to this board (UTC).</param>
/// <param name="ConnectedBy">Who confirmed the server's latest connection, if they still exist.</param>
public sealed record DiscordConnection(
    string GuildId, string GuildName, Guid AgentId, List<DiscordChannelDetail>? Channels, DateTimeOffset ConnectedAt, string? ConnectedBy);

/// <summary>A link pasted in Discord, to preview.</summary>
/// <param name="Url">The link as pasted, including everything after #.</param>
/// <param name="ChannelId">The channel it was pasted in.</param>
public sealed record LinkPreviewRequest(
    [property: Required, StringLength(2000, MinimumLength = 1)] string Url,
    [property: Required, StringLength(DiscordGuild.IdMaxLength, MinimumLength = 1)] string ChannelId);

/// <summary>What a link points to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LinkPreviewKind>))]
public enum LinkPreviewKind
{
    Card,
    Board,
    Invite,
}

/// <summary>A link preview for the Discord bot. Which fields are set depends on <paramref name="Kind"/>.</summary>
/// <param name="Kind">Card, Board or Invite.</param>
/// <param name="BoardName">The board's name (all kinds).</param>
/// <param name="ReadableId">Card: its readable id, e.g. PRIK-109.</param>
/// <param name="Title">Card: its title.</param>
/// <param name="Description">Card: its first note as plain text, or how much of its checklists is done. Board: its description. At most 300 characters; null without one.</param>
/// <param name="Color">Card: its fill.</param>
/// <param name="Path">Card and Board: the path in the web app, after the site's address.</param>
/// <param name="Role">Invite: the role it grants.</param>
/// <param name="InvitedBy">Invite: who created it, if they still exist.</param>
/// <param name="ExpiresAt">Invite: when it stops working (UTC).</param>
public sealed record LinkPreview(
    LinkPreviewKind Kind,
    string BoardName,
    string? ReadableId = null,
    string? Title = null,
    string? Description = null,
    CardColor? Color = null,
    string? Path = null,
    BoardRole? Role = null,
    string? InvitedBy = null,
    DateTimeOffset? ExpiresAt = null);
