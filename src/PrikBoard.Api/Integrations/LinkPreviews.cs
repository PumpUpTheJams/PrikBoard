using System.Text.RegularExpressions;
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

/// <summary>What a chat bot shows for a link or readable id. Which fields are set depends on <paramref name="Kind"/>.</summary>
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
/// <param name="Bucket">Card: the bucket it is in; null when loose on the board.</param>
/// <param name="Tags">Card: its tags.</param>
/// <param name="Assignees">Card: the names of the people and agents on it.</param>
/// <param name="Checklist">Card: how much of its checklists is done ("3/7"), when it has items and a text description too.</param>
/// <param name="WorkingFor">Card: while an agent works on it, the name of the person it works for.</param>
/// <param name="CardId">Card: its id, to find the preview again when it changes.</param>
/// <param name="BoardId">Card and Board: the board's id.</param>
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
    DateTimeOffset? ExpiresAt = null,
    string? Bucket = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Assignees = null,
    string? Checklist = null,
    string? WorkingFor = null,
    Guid? CardId = null,
    Guid? BoardId = null);

/// <summary>
/// Decides what a link or readable id in a chat message previews as, for the chat bots (<see cref="DiscordGateway"/>),
/// looking it up as the workspace's agent. Nothing for every source it won't preview, so silence never tells whether a
/// board exists: another host, a board or card the workspace isn't connected to or that doesn't exist, a channel the
/// board isn't previewed in, or a used, revoked or expired invite.
/// </summary>
public static partial class LinkPreviews
{
    /// <summary>Sources previewed per message; Discord takes 10 embeds, and five is plenty.</summary>
    public const int MaxPerMessage = 5;

    private const string InvitePrefix = "/#/invite/";

    /// <summary>A readable id on its own, as people type it: a board key and a card number, PRIK-149.</summary>
    [GeneratedRegex(@"(?<![\w/#.-])([A-Z][A-Z0-9]{1,9})-([0-9]{1,7})(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex ReadableIdPattern();

    /// <summary>
    /// What a message may preview, in order: links to this PrikBoard (<see cref="LinkFinder"/>), then readable ids outside
    /// those links, at most <see cref="MaxPerMessage"/> together.
    /// </summary>
    public static IReadOnlyList<string> Sources(string text, LinkFinder links)
    {
        var found = links.Find(text).ToList();
        var rest = text;
        foreach (var link in found) rest = rest.Replace(link, " ", StringComparison.Ordinal);
        foreach (Match match in ReadableIdPattern().Matches(rest))
        {
            if (found.Count >= MaxPerMessage) break;
            if (!found.Contains(match.Value)) found.Add(match.Value);
        }
        return found.Take(MaxPerMessage).ToList();
    }

    /// <summary>The preview of a source from <see cref="Sources"/>, found in <paramref name="channelId"/> of workspace <paramref name="workspaceId"/>.</summary>
    public static async Task<LinkPreview?> FindAsync(
        PrikBoardDbContext db, ChatPlatform platform, string workspaceId, string source, string channelId, string? publicUrl, CancellationToken ct)
    {
        var agentId = await AgentIdAsync(db, platform, workspaceId, ct);
        if (agentId is null) return null;

        if (ReadableIdPattern().Match(source) is { Success: true } id && id.Value == source)
        {
            return await ReadableIdAsync(db, platform, workspaceId, agentId.Value, id.Groups[1].Value, int.Parse(id.Groups[2].Value), channelId, ct);
        }
        if (AppPath(source, publicUrl) is not { } path) return null;

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

        if (board is null || !await PreviewsInAsync(db, platform, workspaceId, agentId.Value, board.Id, channelId, ct)) return null;
        return card is null
            ? new LinkPreview(LinkPreviewKind.Board, board.Name, Description: CardPreview.Describe(board), Path: CardLinks.BoardPath(board.ShortId), BoardId: board.Id)
            : await CardAsync(db, card, board, ct);
    }

    /// <summary>
    /// A readable id previews only on boards connected to this channel, and only when one of them has that key: keys aren't
    /// unique, so an id that could mean two boards previews nothing.
    /// </summary>
    private static async Task<LinkPreview?> ReadableIdAsync(
        PrikBoardDbContext db, ChatPlatform platform, string workspaceId, Guid agentId, string key, int number, string channelId, CancellationToken ct)
    {
        var connected = (await db.ChatBoardChannels.AsNoTracking().Where(c => c.Platform == platform && c.WorkspaceId == workspaceId).ToListAsync(ct))
            .Where(c => c.Allows(channelId)).Select(c => c.BoardId).ToList();
        var boards = await db.Boards.AsNoTracking().Where(b => connected.Contains(b.Id) && b.Key == key).ToListAsync(ct);
        if (boards is not [var board] || await BoardAccess.RoleOnBoardAsync(db, board.Id, agentId, ct) is null) return null;
        var card = await db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.BoardId == board.Id && c.Number == number, ct);
        return card is null ? null : await CardAsync(db, card, board, ct);
    }

    /// <summary>Whether the workspace's agent is still on the board and the board previews in this channel.</summary>
    private static async Task<bool> PreviewsInAsync(
        PrikBoardDbContext db, ChatPlatform platform, string workspaceId, Guid agentId, Guid boardId, string channelId, CancellationToken ct)
    {
        if (await BoardAccess.RoleOnBoardAsync(db, boardId, agentId, ct) is null) return false;
        var limit = await db.ChatBoardChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Platform == platform && c.WorkspaceId == workspaceId && c.BoardId == boardId, ct);
        return limit is not null && limit.Allows(channelId);
    }

    /// <summary>The agent of a connected workspace; null for one that was never connected.</summary>
    public static Task<Guid?> AgentIdAsync(PrikBoardDbContext db, ChatPlatform platform, string workspaceId, CancellationToken ct) =>
        db.ChatWorkspaces.Where(w => w.Platform == platform && w.WorkspaceId == workspaceId).Select(w => (Guid?)w.AgentId).FirstOrDefaultAsync(ct);

    /// <summary>A card's preview, with its bucket, tags, people and progress.</summary>
    public static async Task<LinkPreview> CardAsync(PrikBoardDbContext db, Card card, Board board, CancellationToken ct)
    {
        var bucket = card.BucketId is { } bucketId
            ? await db.Buckets.AsNoTracking().Where(b => b.Id == bucketId).Select(b => b.Name).FirstOrDefaultAsync(ct)
            : null;
        var ids = card.AssigneeIds.Append(card.AgentWorkingForId ?? Guid.Empty).Where(i => i != Guid.Empty).Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ShownName, ct);

        var description = CardPreview.Describe(card);
        var items = card.Components.Where(c => c.Kind == CardComponentKind.Checklist).SelectMany(c => c.Items).ToList();
        var progress = $"{items.Count(i => i.Done)}/{items.Count}";
        return new LinkPreview(LinkPreviewKind.Card, board.Name,
            ReadableId: CardLinks.ReadableId(board.Key, card.Number),
            Title: card.Title,
            Description: description,
            Color: card.Color,
            Path: CardLinks.CardPath(board.ShortId, card.Number),
            Bucket: bucket,
            Tags: card.Tags,
            Assignees: card.AssigneeIds.Select(a => names.GetValueOrDefault(a)).OfType<string>().ToList(),
            // Without a text description, the description says this already.
            Checklist: items.Count > 0 && description != $"{progress} done" ? progress : null,
            WorkingFor: card.AgentWorking ? card.AgentWorkingForId is { } by ? names.GetValueOrDefault(by) ?? "" : "" : null,
            CardId: card.Id,
            BoardId: board.Id);
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
