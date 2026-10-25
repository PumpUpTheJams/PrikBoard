namespace PrikBoard.Api.Domain;

/// <summary>
/// A chat app PrikBoard's bots work in. Stored as text, so the names may never change. Each has its own
/// <see cref="Agent"/> issuer (<see cref="ChatPlatforms.Issuer"/>), and its workspaces, channels and messages are keyed
/// by platform, so ids from two chat apps never meet.
/// </summary>
public enum ChatPlatform
{
    Discord,
    Slack,
}

/// <summary>What differs per chat app in the shared chat model.</summary>
public static class ChatPlatforms
{
    /// <summary>The longest chat id kept: Discord's snowflakes (20 digits), Slack's ids and message timestamps fit.</summary>
    public const int IdMaxLength = 32;

    /// <summary>The <see cref="Agent.Issuer"/> of the platform's workspaces.</summary>
    public static string Issuer(ChatPlatform platform) => platform switch
    {
        ChatPlatform.Discord => Agent.DiscordIssuer,
        ChatPlatform.Slack => Agent.SlackIssuer,
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>
    /// A workspace, channel or user id as the platform writes it: Discord's are 1 to 20 digits, Slack's are an uppercase
    /// letter and then uppercase letters and digits (T…, C…, G…, U…).
    /// </summary>
    public static bool IsId(ChatPlatform platform, string? value) => platform switch
    {
        ChatPlatform.Discord => value is { Length: > 0 and <= DiscordIds.MaxLength } && !value.AsSpan().ContainsAnyExceptInRange('0', '9'),
        ChatPlatform.Slack => value is { Length: >= 2 and <= IdMaxLength } && char.IsAsciiLetterUpper(value[0])
                              && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)),
        _ => false,
    };
}

/// <summary>
/// A chat workspace connected to PrikBoard (a Discord server, or a Slack workspace), so the platform's bot can preview
/// links to boards there. The workspace is an <see cref="Agent"/> (issuer <see cref="ChatPlatforms.Issuer"/>, client id
/// the workspace id) that is a Viewer member of each connected board: that membership is the grant, and removing it
/// disconnects the board. This row keeps what the agent can't hold. See docs/discord.md and docs/slack.md.
/// </summary>
public class ChatWorkspace
{
    public ChatPlatform Platform { get; set; }

    /// <summary>The platform's id for the workspace: a Discord guild id or a Slack team id. Not personal data about PrikBoard users.</summary>
    public required string WorkspaceId { get; set; }

    /// <summary>The workspace's agent; its display name is the workspace's name when a board was last connected to it.</summary>
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }

    /// <summary>The board owner who made the latest connection; null once they no longer exist.</summary>
    public Guid? ConnectedById { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Where a connected workspace may preview one board, and what each of those channels does for it: the channels its
/// owners picked, the whole workspace, or both (a channel then only adds its own settings, such as posting updates). The
/// workspace's membership of the board is the grant; this row only narrows it.
/// </summary>
public class ChatBoardChannels
{
    /// <summary>Channels one board can be connected to in a workspace; beyond that, connect it to the whole workspace.</summary>
    public const int MaxChannels = 50;

    public ChatPlatform Platform { get; set; }
    public required string WorkspaceId { get; set; }
    public Guid BoardId { get; set; }

    /// <summary>Whether links to the board preview in every channel of the workspace.</summary>
    public bool WholeWorkspace { get; set; }

    /// <summary>The channels picked for this board, with their settings, named as they were when last seen.</summary>
    public List<ChatChannel> Channels { get; set; } = [];

    /// <summary>Whether a link to the board may be previewed in this channel.</summary>
    public bool Allows(string channelId) => WholeWorkspace || Channels.Any(c => c.Id == channelId);

    /// <summary>The settings of a channel picked for this board; null for one that isn't (also under the whole workspace).</summary>
    public ChatChannel? Channel(string channelId) => Channels.FirstOrDefault(c => c.Id == channelId);
}

/// <summary>A chat channel connected to a board: its id, its name when PrikBoard last heard it, and what it does there.</summary>
/// <param name="Id">The platform's id for the channel.</param>
/// <param name="Name">Its name, without the #.</param>
/// <param name="Updates">What the bot posts in the channel as it happens on the board.</param>
/// <param name="SaveBucketId">Where "Save to PrikBoard" in this channel puts cards; null for the board's first bucket.</param>
public sealed record ChatChannel(string Id, string Name, ChatUpdates Updates = ChatUpdates.None, Guid? SaveBucketId = null);

/// <summary>The board activity a channel can follow. Stored as a number, so the names may change but never the values.</summary>
[Flags]
public enum ChatUpdates
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
/// A chat user linked to a PrikBoard user, so what they do through the bot (Save to PrikBoard) is done as that person,
/// with their own role on the board. Only a keyed hash of the chat user id is stored (<see cref="Auth.KeyedHasher"/>),
/// like sign-in subjects. People who sign in with the platform's sign-in provider need no link: their sign-in already
/// says who they are. One link per person and platform.
/// </summary>
public class ChatAccount
{
    public ChatPlatform Platform { get; set; }

    /// <summary>HMAC of the chat user id (<c>KeyedHasher.Subject(ChatAccounts.HashProvider(platform), id)</c>).</summary>
    public required byte[] UserHash { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A message with link previews, remembered for <see cref="KeptFor"/> so the bot can update the previews when the cards
/// change, or say a link is no longer available once its board is disconnected. In Discord it is the bot's own reply; in
/// Slack the message whose links it unfurled.
/// </summary>
public class ChatPreviewMessage
{
    /// <summary>How long posted previews follow their cards.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    public ChatPlatform Platform { get; set; }
    public required string WorkspaceId { get; set; }
    public required string ChannelId { get; set; }

    /// <summary>The message: Discord's message id, or Slack's message timestamp (unique within its channel).</summary>
    public required string MessageId { get; set; }

    /// <summary>What was previewed, in the message's order: pasted links, or bare readable ids (PRIK-149).</summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>The cards and boards the message shows, to find it when one of them changes.</summary>
    public List<Guid> CardIds { get; set; } = [];
    public List<Guid> BoardIds { get; set; } = [];

    public DateTimeOffset PostedAt { get; set; } = DateTimeOffset.UtcNow;
}
