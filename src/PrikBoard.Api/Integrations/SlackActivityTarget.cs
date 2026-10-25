using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// The Slack app's part in <see cref="ChatActivity"/>: it unfurls a remembered message's links again to match their
/// cards (<c>chat.unfurl</c> on the same message), and posts a board's updates as the bot (<c>chat.postMessage</c>), both
/// drawn by <see cref="SlackBlocks"/>.
/// </summary>
public sealed class SlackActivityTarget(ISlackGateway gateway, SlackApp app) : IChatActivityTarget
{
    public ChatPlatform Platform => ChatPlatform.Slack;

    public string? OnlineAt => gateway.State == SlackAppState.Online ? app.Current.PublicUrl : null;

    public Task<bool> RefreshAsync(ChatPreviewMessage message, IReadOnlyList<LinkPreview?> previews, CancellationToken ct)
    {
        var blocks = new SlackBlocks(OnlineAt!);
        var unfurls = new Dictionary<string, object>();
        var unavailable = new Dictionary<string, object>();
        for (var i = 0; i < previews.Count; i++)
        {
            unfurls[message.Sources[i]] = previews[i] is { } preview ? blocks.For(preview, message.Sources[i]) : blocks.Unavailable();
            unavailable[message.Sources[i]] = blocks.Unavailable();
        }
        return gateway.RefreshUnfurlsAsync(message.ChannelId, message.MessageId, unfurls, unavailable, ct);
    }

    public Task PostUpdatesAsync(string channelId, Board board, IReadOnlyList<ChatUpdateLine> lines, CancellationToken ct) =>
        gateway.PostAsync(channelId, $"Updates on {board.Name}",
            new SlackBlocks(OnlineAt!).Updates(board.Name, CardLinks.BoardPath(board.ShortId), lines), ct);
}
