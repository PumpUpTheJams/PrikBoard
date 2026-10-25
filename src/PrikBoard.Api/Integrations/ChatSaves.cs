using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Endpoints;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Integrations;

/// <summary>A chat message to save as a card, as the bot read it.</summary>
/// <param name="Text">Its text, with mentions as names (Discord's clean content, <see cref="SlackText.Plain"/> for Slack).</param>
/// <param name="AuthorName">Who wrote it, as the chat app shows them.</param>
/// <param name="ChannelName">The channel it was in, without the #.</param>
/// <param name="JumpUrl">The link that opens it in the chat app.</param>
/// <param name="SentAt">When it was sent.</param>
/// <param name="Attachments">Its files; images among them are attached to the card.</param>
/// <param name="Platform">The chat app it is from, for the link's name and where its images may be fetched.</param>
public sealed record SavedMessage(
    string Text, string AuthorName, string ChannelName, string JumpUrl, DateTimeOffset SentAt, IReadOnlyList<SavedAttachment> Attachments,
    ChatPlatform Platform = ChatPlatform.Discord);

/// <summary>A file on a chat message.</summary>
public sealed record SavedAttachment(string Url, string FileName, string? ContentType, long Size);

/// <summary>A board someone may save to from a chat workspace, with its buckets and the one picked first.</summary>
public sealed record SaveTarget(Guid BoardId, string Name, IReadOnlyList<(Guid Id, string Name)> Buckets, Guid? DefaultBucketId);

/// <summary>
/// Save to PrikBoard, apart from the chat apps themselves so tests can run it: which boards a person may save to from a
/// workspace, and the card a message becomes. The card is created through createCard as the person
/// (<see cref="CardHistory.ActingAs"/>), so their role decides and the card's history names them. Images come from the
/// chat app's own file hosts only: Discord's CDN, or Slack's <c>files.slack.com</c> with the bot token (and the configured
/// <c>Slack:ApiUrl</c>'s host, for the fake Slack).
/// </summary>
public sealed class ChatSaves(IHttpClientFactory http, FileStore files, BoardEvents events, IConfiguration config, ILogger<ChatSaves> logger)
{
    /// <summary>The HTTP client that fetches message images from the chat apps' file hosts.</summary>
    public const string AttachmentsClient = "chat-attachments";

    /// <summary>Images taken from one message.</summary>
    public const int MaxImages = 4;

    private static readonly string[] DiscordHosts = ["cdn.discordapp.com", "media.discordapp.net"];
    private static readonly string[] SlackHosts = ["files.slack.com"];

    /// <summary>
    /// The boards <paramref name="userId"/> can edit that are connected to this workspace, the ones in this channel first,
    /// each with the bucket the channel saves to (or the first one).
    /// </summary>
    public static async Task<List<SaveTarget>> TargetsAsync(
        PrikBoardDbContext db, Guid userId, ChatPlatform platform, string workspaceId, string channelId, CancellationToken ct)
    {
        var agentId = await LinkPreviews.AgentIdAsync(db, platform, workspaceId, ct);
        if (agentId is null) return [];
        var limits = await db.ChatBoardChannels.AsNoTracking().Where(c => c.Platform == platform && c.WorkspaceId == workspaceId).ToListAsync(ct);
        var targets = new List<(SaveTarget Target, bool Here)>();
        foreach (var limit in limits)
        {
            if (!await ChatPreviews.CanSeeAsync(db, agentId.Value, limit.BoardId, ct)) continue;
            if (await BoardAccess.RoleOnBoardAsync(db, limit.BoardId, userId, ct) is not >= BoardRole.Editor) continue;
            if (await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == limit.BoardId, ct) is not { } board) continue;
            var buckets = Layout.InListOrder(await db.Buckets.AsNoTracking().Where(b => b.BoardId == board.Id).ToListAsync(ct))
                .Select(b => (b.Id, b.Name)).ToList();
            var channel = limit.Channel(channelId);
            var bucketId = channel?.SaveBucketId is { } saved && buckets.Any(b => b.Id == saved) ? saved : buckets.FirstOrDefault().Id;
            targets.Add((new SaveTarget(board.Id, board.Name, buckets, bucketId == Guid.Empty ? null : bucketId), limit.Allows(channelId)));
        }
        return targets
            .OrderByDescending(t => t.Here)
            .ThenBy(t => t.Target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Target)
            .ToList();
    }

    /// <summary>A title from a message: its first line without Markdown marks, or where it came from.</summary>
    public static string TitleFor(SavedMessage message)
    {
        var line = message.Text.Split('\n').Select(l => l.Trim().TrimStart('#', '>', '-', '*', ' ').Trim()).FirstOrDefault(l => l.Length > 0);
        var title = line ?? $"From #{message.ChannelName}";
        return title.Length <= 100 ? title : title[..99] + "…";
    }

    /// <summary>
    /// Creates the card as <paramref name="userId"/>: the message as a Text component, a link back to it, and its images.
    /// Null with a reason when it couldn't be saved.
    /// </summary>
    /// <remarks><paramref name="filesToken"/> is the bearer token Slack's file host wants (the bot token); Discord's needs none.</remarks>
    public async Task<(CardDetail? Card, string? Problem)> SaveAsync(
        PrikBoardDbContext db, Guid userId, Guid boardId, Guid? bucketId, string title, SavedMessage message, CancellationToken ct,
        string? filesToken = null)
    {
        if (await BoardAccess.RoleOnBoardAsync(db, boardId, userId, ct) is not >= BoardRole.Editor)
        {
            return (null, "You can't add cards to that board any more.");
        }

        var quote = string.Join('\n', message.Text.Split('\n').Select(l => "> " + l));
        var text = (message.Text.Length > 0 ? quote + "\n\n" : "") +
                   $"From **{message.AuthorName}** in #{message.ChannelName}, {message.SentAt:yyyy-MM-dd HH:mm} UTC";
        if (text.Length > CardComponent.MaxTextLength) text = text[..(CardComponent.MaxTextLength - 1)] + "…";
        var request = new CreateCardRequest(
            title.Trim().Length == 0 ? TitleFor(message) : title.Trim(),
            BucketId: bucketId,
            Components:
            [
                new NewCardComponent(CardComponentKind.Text, Text: text),
                new NewCardComponent(CardComponentKind.Links, Links: [new CardLink($"Message in {message.Platform}", message.JumpUrl)]),
            ]);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserPrincipal.IdClaim, userId.ToString())], "chat", UserPrincipal.IdClaim, null));
        CardDetail card;
        using (CardHistory.ActingAs(userId))
        {
            var created = await CardEndpoints.CreateCardAsync(boardId, request, principal, db, assigneesChecked: true, ct);
            if (created.Result is not Microsoft.AspNetCore.Http.HttpResults.Created<CardDetail> { Value: { } value })
            {
                return (null, "PrikBoard couldn't add the card; try again in PrikBoard itself.");
            }
            card = value;

            foreach (var attachment in message.Attachments.Where(a => IsImage(a, message.Platform)).Take(MaxImages))
            {
                await AttachAsync(db, userId, boardId, card.Id, attachment, message.Platform == ChatPlatform.Slack ? filesToken : null, ct);
            }
        }
        await events.ChangedAsync(boardId, userId);
        return (card, null);
    }

    private bool IsImage(SavedAttachment attachment, ChatPlatform platform) =>
        attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
        && attachment.Size <= ImageFormats.MaxBytes
        && Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps && (platform == ChatPlatform.Slack ? SlackHosts : DiscordHosts).Contains(url.Host, StringComparer.OrdinalIgnoreCase)
            || platform == ChatPlatform.Slack && IsConfiguredSlackHost(url));

    /// <summary>The configured Slack API's own host (the fake Slack in tools/FakeSlack serves its files there).</summary>
    private bool IsConfiguredSlackHost(Uri url) =>
        Uri.TryCreate(config[SlackApi.ApiUrlSetting], UriKind.Absolute, out var api)
        && url.Scheme == api.Scheme && string.Equals(url.Authority, api.Authority, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches an image from the chat app and attaches it to the card; one that fails is left out. HttpClient drops the
    /// bearer token on redirects, so it never reaches another host.
    /// </summary>
    private async Task AttachAsync(
        PrikBoardDbContext db, Guid userId, Guid boardId, Guid cardId, SavedAttachment attachment, string? bearer, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, attachment.Url);
            if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var response = await http.CreateClient(AttachmentsClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await ImageEndpoints.AddImage(boardId, body, userId, db, files, ct, attachment.FileName, cardId);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(error, "An image of a saved chat message could not be fetched.");
        }
    }
}
