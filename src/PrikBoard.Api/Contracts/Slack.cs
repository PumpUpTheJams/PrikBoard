using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Integrations;

namespace PrikBoard.Api.Contracts;

/// <summary>A Slack channel connected to a board, with what it does there.</summary>
/// <param name="Id">Slack's id for it (C… or G…).</param>
/// <param name="Name">Its name, without the #.</param>
/// <param name="Updates">What the app posts there as it happens on the board.</param>
/// <param name="SaveBucketId">Where Save as card in this channel puts cards; null for the board's first bucket.</param>
public sealed record SlackChannelDetail(string Id, string Name, List<ChatUpdate> Updates, Guid? SaveBucketId)
{
    public static List<SlackChannelDetail> From(IEnumerable<ChatChannel> channels) =>
        channels.Select(c => new SlackChannelDetail(c.Id, c.Name, ChatUpdateLists.From(c.Updates), c.SaveBucketId)).ToList();
}

/// <summary>What a Slack channel connected to a board does for it. Both fields replace what was set.</summary>
/// <param name="Updates">The board activity the app posts in the channel; empty for none.</param>
/// <param name="SaveBucketId">The bucket Save as card in this channel puts cards in; null for the board's first bucket.</param>
public sealed record UpdateSlackChannelRequest([property: Required] List<ChatUpdate> Updates, Guid? SaveBucketId = null);

/// <summary>A Slack workspace connected to a board, for its owners.</summary>
/// <param name="TeamId">Slack's id for the workspace (T…).</param>
/// <param name="TeamName">Its name.</param>
/// <param name="AgentId">The workspace's member on the board (a Viewer agent).</param>
/// <param name="WholeWorkspace">Whether the board's links preview in every channel of the workspace.</param>
/// <param name="Channels">The channels picked for the board, where its links preview.</param>
/// <param name="ConnectedAt">When it was connected to this board (UTC).</param>
/// <param name="ConnectedBy">Who made the workspace's latest connection, if they still exist.</param>
public sealed record SlackConnection(
    string TeamId, string TeamName, Guid AgentId, bool WholeWorkspace, List<SlackChannelDetail> Channels, DateTimeOffset ConnectedAt, string? ConnectedBy);

/// <summary>A channel the Slack app is in, which a board can be previewed in.</summary>
/// <param name="Id">Slack's id for it.</param>
/// <param name="Name">Its name, without the #.</param>
public sealed record SlackChannelChoice(string Id, string Name);

/// <summary>The Slack workspace the app is installed in, with the channels a board can be previewed in.</summary>
/// <param name="TeamId">Slack's id for the workspace.</param>
/// <param name="Name">Its name.</param>
/// <param name="Channels">The channels the app was added to, by name; channels shared with other organizations (Slack Connect) are left out.</param>
public sealed record SlackWorkspaceChoice(string TeamId, string Name, List<SlackChannelChoice> Channels);

/// <summary>Where the instance's Slack app can preview a board's links.</summary>
/// <param name="State">Off (no admin set up the app), Connecting, Online or Failed.</param>
/// <param name="Workspaces">The workspace the app is in; empty while it is not online.</param>
public sealed record SlackWorkspaces(SlackAppState State, List<SlackWorkspaceChoice> Workspaces);

/// <summary>Connects a board to a Slack channel, or to the whole workspace.</summary>
/// <param name="TeamId">The workspace, from listSlackWorkspaces.</param>
/// <param name="ChannelId">A channel of it, from listSlackWorkspaces; null for every channel. Channels and the whole workspace can be combined.</param>
public sealed record ConnectSlackRequest(
    [property: Required, StringLength(ChatPlatforms.IdMaxLength, MinimumLength = 1)] string TeamId,
    [property: StringLength(ChatPlatforms.IdMaxLength, MinimumLength = 1)] string? ChannelId = null);

/// <summary>Where the Slack app's tokens come from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlackAppSource>))]
public enum SlackAppSource
{
    /// <summary>Slack:BotToken and Slack:AppToken in the server's configuration: read-only here.</summary>
    Configuration,

    /// <summary>Saved on the admin page.</summary>
    Setup,
}

/// <summary>A board a Slack workspace is connected to.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="ShortId">Its short id, as in its links.</param>
/// <param name="BoardName">Its name.</param>
/// <param name="WholeWorkspace">Whether its links preview in every channel of the workspace.</param>
/// <param name="Channels">The channels picked for it.</param>
public sealed record SlackBoardConnection(Guid BoardId, string ShortId, string BoardName, bool WholeWorkspace, List<SlackChannelDetail> Channels);

/// <summary>The workspace the Slack app is in, for admins.</summary>
/// <param name="TeamId">Slack's id for the workspace.</param>
/// <param name="Name">Its name.</param>
/// <param name="ChannelCount">Channels the app was added to.</param>
/// <param name="Boards">The boards previewed there.</param>
public sealed record SlackAppWorkspace(string TeamId, string Name, int ChannelCount, List<SlackBoardConnection> Boards);

/// <summary>The instance's Slack app, for admins. The tokens are never returned.</summary>
/// <param name="State">Off, Connecting, Online or Failed.</param>
/// <param name="Problem">Why it failed, or why it is still connecting.</param>
/// <param name="Source">Where the tokens come from; null without them.</param>
/// <param name="TeamId">The workspace the bot token belongs to.</param>
/// <param name="TeamName">Its name.</param>
/// <param name="BotName">The bot's name in Slack.</param>
/// <param name="PublicUrl">The PrikBoard address pasted links are matched against; its host is the app's unfurl domain.</param>
/// <param name="PublicUrlIsConfigured">Whether that comes from Slack:PublicUrl in the server's configuration (read-only here).</param>
/// <param name="UpdatedAt">When an admin last saved the settings.</param>
/// <param name="Manifest">The app manifest (JSON) to create the Slack app from: Socket Mode, the link_shared event, its scopes and unfurl domain.</param>
/// <param name="CreateAppUrl">Slack's page for creating an app, with the manifest filled in.</param>
/// <param name="Workspaces">The workspace the app is in, with the boards previewed there.</param>
public sealed record SlackAppDetail(
    SlackAppState State,
    string? Problem,
    SlackAppSource? Source,
    string? TeamId,
    string? TeamName,
    string? BotName,
    string? PublicUrl,
    bool PublicUrlIsConfigured,
    DateTimeOffset? UpdatedAt,
    string Manifest,
    string CreateAppUrl,
    List<SlackAppWorkspace> Workspaces);

/// <summary>Sets up the instance's Slack app. Fields left out stay as they are; the first time, both tokens are needed.</summary>
/// <param name="BotToken">The Bot User OAuth Token (xoxb-…) from the app's OAuth &amp; Permissions page. Checked with Slack, then stored encrypted.</param>
/// <param name="AppToken">An app-level token (xapp-…) with connections:write, from Basic Information → App-Level Tokens, for Socket Mode. Checked with Slack, then stored encrypted.</param>
/// <param name="PublicUrl">The PrikBoard address people paste links from, such as https://board.example.com. Defaults to the address this request came to.</param>
public sealed record SetSlackAppRequest(
    [property: StringLength(300, MinimumLength = 1)] string? BotToken = null,
    [property: StringLength(300, MinimumLength = 1)] string? AppToken = null,
    [property: StringLength(SlackAppSettings.PublicUrlMaxLength, MinimumLength = 1)] string? PublicUrl = null);

/// <summary>Where your Slack account is known from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlackAccountSource>))]
public enum SlackAccountSource
{
    /// <summary>You sign in to PrikBoard with Slack, which says who you are.</summary>
    SignIn,

    /// <summary>You linked it from the app's "Connect your PrikBoard account".</summary>
    Link,
}

/// <summary>Whether the Slack app knows who you are in Slack, for Save as card.</summary>
/// <param name="Linked">Whether it does.</param>
/// <param name="Source">How: a Slack sign-in, or a link you made; null when not linked.</param>
/// <param name="LinkedAt">When you made the link (Link only).</param>
public sealed record SlackAccountStatus(bool Linked, SlackAccountSource? Source, DateTimeOffset? LinkedAt);

/// <summary>The Slack user behind a link code, so you can check it is you before linking.</summary>
/// <param name="SlackName">Their name in Slack, as Slack gave it to the app.</param>
/// <param name="WorkspaceName">The Slack workspace the code was asked for in.</param>
/// <param name="ExpiresAt">When the code stops working (UTC).</param>
public sealed record SlackAccountLinkPreview(string SlackName, string WorkspaceName, DateTimeOffset ExpiresAt);

/// <summary>Links the Slack account behind a code from the app to you.</summary>
/// <param name="Code">The code from the app's "Connect your PrikBoard account" link.</param>
public sealed record LinkSlackAccountRequest([property: Required, StringLength(100, MinimumLength = 1)] string Code);
