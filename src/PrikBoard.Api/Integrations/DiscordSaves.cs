using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Endpoints;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Integrations;

/// <summary>A Discord message to save as a card, as the bot read it.</summary>
/// <param name="Text">Its text, with mentions as names (Discord's clean content).</param>
/// <param name="AuthorName">Who wrote it, as Discord shows them.</param>
/// <param name="ChannelName">The channel it was in, without the #.</param>
/// <param name="JumpUrl">The link that opens it in Discord.</param>
/// <param name="SentAt">When it was sent.</param>
/// <param name="Attachments">Its files; images among them are attached to the card.</param>
public sealed record SavedMessage(
    string Text, string AuthorName, string ChannelName, string JumpUrl, DateTimeOffset SentAt, IReadOnlyList<SavedAttachment> Attachments);

/// <summary>A file on a Discord message.</summary>
public sealed record SavedAttachment(string Url, string FileName, string? ContentType, long Size);

/// <summary>A board someone may save to from a Discord server, with its buckets and the one picked first.</summary>
public sealed record SaveTarget(Guid BoardId, string Name, IReadOnlyList<(Guid Id, string Name)> Buckets, Guid? DefaultBucketId);

/// <summary>
/// Save to PrikBoard, apart from Discord itself so tests can run it: which boards a person may save to from a server,
/// and the card a message becomes. The card is created through createCard as the person (<see cref="CardHistory.ActingAs"/>),
/// so their role decides and the card's history names them.
/// </summary>
public sealed class DiscordSaves(IHttpClientFactory http, FileStore files, BoardEvents events, ILogger<DiscordSaves> logger)
{
    /// <summary>The HTTP client that fetches message images from Discord's file hosts.</summary>
    public const string AttachmentsClient = "discord-attachments";

    /// <summary>Images taken from one message.</summary>
    public const int MaxImages = 4;

    private static readonly string[] AttachmentHosts = ["cdn.discordapp.com", "media.discordapp.net"];

    /// <summary>
    /// The boards <paramref name="userId"/> can edit that are connected to this server, the ones in this channel first,
    /// each with the bucket the channel saves to (or the first one).
    /// </summary>
    public static async Task<List<SaveTarget>> TargetsAsync(PrikBoardDbContext db, Guid userId, string guildId, string channelId, CancellationToken ct)
    {
        var agentId = await db.DiscordGuilds.Where(g => g.GuildId == guildId).Select(g => (Guid?)g.AgentId).FirstOrDefaultAsync(ct);
        if (agentId is null) return [];
        var limits = await db.DiscordBoardChannels.AsNoTracking().Where(c => c.GuildId == guildId).ToListAsync(ct);
        var targets = new List<(SaveTarget Target, bool Here)>();
        foreach (var limit in limits)
        {
            if (!await DiscordPreviews.CanSeeAsync(db, agentId.Value, limit.BoardId, ct)) continue;
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
    public async Task<(CardDetail? Card, string? Problem)> SaveAsync(
        PrikBoardDbContext db, Guid userId, Guid boardId, Guid? bucketId, string title, SavedMessage message, CancellationToken ct)
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
                new NewCardComponent(CardComponentKind.Links, Links: [new CardLink("Message in Discord", message.JumpUrl)]),
            ]);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserPrincipal.IdClaim, userId.ToString())], "discord", UserPrincipal.IdClaim, null));
        CardDetail card;
        using (CardHistory.ActingAs(userId))
        {
            var created = await CardEndpoints.CreateCardAsync(boardId, request, principal, db, assigneesChecked: true, ct);
            if (created.Result is not Microsoft.AspNetCore.Http.HttpResults.Created<CardDetail> { Value: { } value })
            {
                return (null, "PrikBoard couldn't add the card; try again in PrikBoard itself.");
            }
            card = value;

            foreach (var attachment in message.Attachments.Where(IsImage).Take(MaxImages))
            {
                await AttachAsync(db, userId, boardId, card.Id, attachment, ct);
            }
        }
        await events.ChangedAsync(boardId, userId);
        return (card, null);
    }

    private static bool IsImage(SavedAttachment attachment) =>
        attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
        && attachment.Size <= ImageFormats.MaxBytes
        && Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps
        && AttachmentHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fetches an image from Discord and attaches it to the card; one that fails is left out.</summary>
    private async Task AttachAsync(PrikBoardDbContext db, Guid userId, Guid boardId, Guid cardId, SavedAttachment attachment, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient(AttachmentsClient).GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await ImageEndpoints.AddImage(boardId, body, userId, db, files, ct, attachment.FileName, cardId);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(error, "An image of a saved Discord message could not be fetched.");
        }
    }
}
