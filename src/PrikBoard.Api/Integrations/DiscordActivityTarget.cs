using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// The Discord bot's part in <see cref="ChatActivity"/>: it edits its remembered replies to match their cards, and posts
/// a board's updates as one embed (<see cref="Embeds"/>).
/// </summary>
public sealed class DiscordActivityTarget(IDiscordGateway gateway, DiscordBot bot) : IChatActivityTarget
{
    public ChatPlatform Platform => ChatPlatform.Discord;

    public string? OnlineAt => gateway.State == DiscordBotState.Online ? bot.Current.PublicUrl : null;

    public Task<bool> RefreshAsync(ChatPreviewMessage message, IReadOnlyList<LinkPreview?> previews, CancellationToken ct)
    {
        var embeds = new Embeds(OnlineAt!);
        var shown = previews.Select((p, i) => p is null ? embeds.Unavailable() : embeds.For(p, message.Sources[i])).ToList();
        return gateway.EditAsync(message.ChannelId, message.MessageId, shown);
    }

    public async Task PostUpdatesAsync(string channelId, Board board, IReadOnlyList<ChatUpdateLine> lines, CancellationToken ct)
    {
        var embeds = new Embeds(OnlineAt!);
        await gateway.PostAsync(channelId, [embeds.Updates(board.Name, CardLinks.BoardPath(board.ShortId), lines.Select(embeds.UpdateLine).ToList())]);
    }
}
