using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Endpoints;

namespace PrikBoard.Api.Integrations;

/// <summary>What a pasted link points to.</summary>
public enum LinkPreviewKind
{
    Card,
    Board,
    Invite,
}

/// <summary>What the Discord bot shows for a link. Which fields are set depends on <paramref name="Kind"/>.</summary>
/// <param name="Kind">Card, Board or Invite.</param>
/// <param name="BoardName">The board's name (all kinds).</param>
/// <param name="ReadableId">Card: its readable id, e.g. PRIK-109.</param>
/// <param name="Title">Card: its title.</param>
/// <param name="Description">Card: its first Text component as plain text, or, without one, how much of its checklists is done. Board: its description. At most 300 characters; null without one.</param>
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

/// <summary>
/// Decides what a link pasted in a Discord channel previews as, for the bot in <see cref="DiscordGateway"/>. Nothing for
/// every link it won't preview, so silence never tells whether a board exists: another host, a board or card the server
/// isn't connected to or that doesn't exist, a channel the board isn't previewed in, or a used, revoked or expired invite.
/// </summary>
public static class LinkPreviews
{
    private const string InvitePrefix = "/#/invite/";

    /// <summary>The preview of <paramref name="url"/>, pasted in <paramref name="channelId"/> of server <paramref name="guildId"/>.</summary>
    public static async Task<LinkPreview?> FindAsync(
        PrikBoardDbContext db, string guildId, string url, string channelId, string? publicUrl, CancellationToken ct)
    {
        if (AppPath(url, publicUrl) is not { } path) return null;
        var agentId = await db.DiscordGuilds.Where(g => g.GuildId == guildId).Select(g => (Guid?)g.AgentId).FirstOrDefaultAsync(ct);
        if (agentId is null) return null;

        // Anyone holding an invite link is meant to see the board's name, so invites need no membership or channel.
        if (path.StartsWith(InvitePrefix, StringComparison.Ordinal))
        {
            var token = path[InvitePrefix.Length..];
            if (token.Length == 0 || token.Length > BoardInvite.MaxTokenLength || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
            if (await InviteEndpoints.FindOpenAsync(db, token, ct) is not { } found) return null;
            var (invite, invited) = found;
            return new LinkPreview(LinkPreviewKind.Invite, invited.Name,
                Role: invite.Role, InvitedBy: invite.CreatedBy?.ShownName, ExpiresAt: invite.ExpiresAt);
        }

        Board? board;
        Card? card = null;
        if (CardLinks.TryParse(path, out var linked))
        {
            var cards = db.Cards.AsNoTracking().Include(c => c.Board);
            card = linked.ByNumber
                ? await cards.FirstOrDefaultAsync(c => c.Board!.ShortId == linked.BoardShortId && c.Number == linked.Number, ct)
                : await cards.FirstOrDefaultAsync(c => c.Id == linked.CardId, ct);
            if (card is null) return null;
            board = card.Board;
        }
        else if (CardLinks.TryParseBoard(path, out var shortId, out var boardId))
        {
            board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => shortId == null ? b.Id == boardId : b.ShortId == shortId, ct);
        }
        else
        {
            return null;
        }

        if (board is null || await BoardAccess.RoleOnBoardAsync(db, board.Id, agentId.Value, ct) is null) return null;
        var limit = await db.DiscordBoardChannels.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId && c.BoardId == board.Id, ct);
        if (limit is null || !limit.Allows(channelId)) return null;

        return card is null
            ? new LinkPreview(LinkPreviewKind.Board, board.Name, Description: CardPreview.Describe(board), Path: CardLinks.BoardPath(board.ShortId))
            : new LinkPreview(LinkPreviewKind.Card, board.Name,
                ReadableId: CardLinks.ReadableId(board.Key, card.Number),
                Title: card.Title,
                Description: CardPreview.Describe(card),
                Color: card.Color,
                Path: CardLinks.CardPath(board.ShortId, card.Number));
    }

    /// <summary>
    /// The web app's path a link holds ("/#/boards/…"), when it is a link to this PrikBoard: same scheme and host as the
    /// public URL, at its path. Null for anything else, and for every link while there is no public URL.
    /// </summary>
    public static string? AppPath(string url, string? publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var site)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var link)
            || link.Scheme != site.Scheme
            || !string.Equals(link.Authority, site.Authority, StringComparison.OrdinalIgnoreCase)
            || link.UserInfo.Length > 0
            || link.Query.Length > 0
            || link.AbsolutePath.TrimEnd('/') != site.AbsolutePath.TrimEnd('/')
            || !link.Fragment.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }
        return "/" + link.Fragment;
    }
}
