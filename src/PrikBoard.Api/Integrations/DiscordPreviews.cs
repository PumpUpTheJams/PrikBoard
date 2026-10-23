using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// The Discord bot's read-only side, apart from Discord itself so tests can run it: what a message previews, remembering
/// the reply so <see cref="DiscordActivity"/> can bring it up to date, and the snapshot of /prikboard board. Everything
/// is looked up as the server's agent, in the channel the message is in.
/// </summary>
public static class DiscordPreviews
{
    /// <summary>
    /// The sources in <paramref name="text"/> that preview here, each with its preview. <paramref name="allow"/> is asked
    /// before each lookup (the bot's rate limit per server); a refusal ends the list.
    /// </summary>
    public static async Task<List<(string Source, LinkPreview Preview)>> ForMessageAsync(
        PrikBoardDbContext db, string guildId, string channelId, string text, LinkFinder links, string publicUrl, CancellationToken ct,
        Func<bool>? allow = null)
    {
        var shown = new List<(string, LinkPreview)>();
        foreach (var source in LinkPreviews.Sources(text, links))
        {
            if (allow is not null && !allow()) break;
            if (await LinkPreviews.FindAsync(db, guildId, source, channelId, publicUrl, ct) is { } preview) shown.Add((source, preview));
        }
        return shown;
    }

    /// <summary>Remembers a reply with previews, so it follows its cards for <see cref="DiscordPreviewMessage.KeptFor"/>.</summary>
    public static async Task RememberAsync(
        PrikBoardDbContext db, string messageId, string guildId, string channelId, IReadOnlyList<(string Source, LinkPreview Preview)> shown,
        CancellationToken ct)
    {
        db.DiscordPreviewMessages.Add(new DiscordPreviewMessage
        {
            MessageId = messageId,
            GuildId = guildId,
            ChannelId = channelId,
            Sources = shown.Select(s => s.Source).ToList(),
            CardIds = shown.Select(s => s.Preview.CardId).OfType<Guid>().Distinct().ToList(),
            BoardIds = shown.Select(s => s.Preview.BoardId).OfType<Guid>().Distinct().ToList(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A remembered reply's previews as they are now, in its order; null where a source previews nothing any more.</summary>
    public static async Task<List<LinkPreview?>> RefreshAsync(PrikBoardDbContext db, DiscordPreviewMessage message, string publicUrl, CancellationToken ct)
    {
        var previews = new List<LinkPreview?>();
        foreach (var source in message.Sources)
        {
            previews.Add(await LinkPreviews.FindAsync(db, message.GuildId, source, message.ChannelId, publicUrl, ct));
        }
        return previews;
    }

    /// <summary>
    /// The boards connected to this channel that the server can still see, those picked for the channel itself first;
    /// then those connected to the whole server, by name.
    /// </summary>
    public static async Task<List<Board>> BoardsInChannelAsync(PrikBoardDbContext db, string guildId, string channelId, CancellationToken ct)
    {
        var agentId = await db.DiscordGuilds.Where(g => g.GuildId == guildId).Select(g => (Guid?)g.AgentId).FirstOrDefaultAsync(ct);
        if (agentId is null) return [];
        var limits = (await db.DiscordBoardChannels.AsNoTracking().Where(c => c.GuildId == guildId).ToListAsync(ct))
            .Where(c => c.Allows(channelId)).ToList();
        var ids = limits.Select(l => l.BoardId).ToList();
        var member = await db.BoardMembers.Where(m => m.PrincipalId == agentId && ids.Contains(m.BoardId)).Select(m => m.BoardId).ToListAsync(ct);
        var boards = await db.Boards.AsNoTracking().Where(b => member.Contains(b.Id)).ToListAsync(ct);
        return boards
            .OrderBy(b => limits.First(l => l.BoardId == b.Id).Channel(channelId) is null)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The snapshot of a board for /prikboard board: its open buckets in list order, who is at work, and the last day.</summary>
    public static async Task<BoardSnapshot> SnapshotAsync(PrikBoardDbContext db, Board board, DateTimeOffset now, CancellationToken ct)
    {
        var buckets = Layout.InListOrder(await db.Buckets.AsNoTracking().Where(b => b.BoardId == board.Id).ToListAsync(ct));
        var cards = await db.Cards.AsNoTracking().Where(c => c.BoardId == board.Id)
            .Select(c => new { c.Id, c.Number, c.Title, c.BucketId, c.X, c.Y, c.AgentWorking, c.AgentWorkingForId })
            .ToListAsync(ct);
        BoardSnapshot.CardRef Ref(int number, string title) =>
            new(CardLinks.ReadableId(board.Key, number), CardLinks.CardPath(board.ShortId, number), title);

        var inBuckets = buckets.Select(b =>
        {
            var held = cards.Where(c => c.BucketId == b.Id).OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
            return new BoardSnapshot.BucketCards(b.Name, held.Count, held.Take(5).Select(c => Ref(c.Number, c.Title)).ToList());
        }).ToList();

        var forIds = cards.Where(c => c.AgentWorking && c.AgentWorkingForId != null).Select(c => c.AgentWorkingForId!.Value).Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => forIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ShownName, ct);
        var working = cards.Where(c => c.AgentWorking).OrderBy(c => c.Number)
            .Select(c => new BoardSnapshot.WorkingCard(CardLinks.ReadableId(board.Key, c.Number), CardLinks.CardPath(board.ShortId, c.Number), c.Title,
                c.AgentWorkingForId is { } by ? names.GetValueOrDefault(by) : null))
            .ToList();

        var since = now.AddDays(-1);
        var entries = (await db.CardChanges.AsNoTracking().Where(c => c.BoardId == board.Id && c.At >= since).ToListAsync(ct))
            .SelectMany(c => c.Entries).ToList();
        return new BoardSnapshot(board.Name, CardLinks.BoardPath(board.ShortId), inBuckets, working,
            entries.Count(e => e.Kind is CardChangeKind.Created or CardChangeKind.Copied or CardChangeKind.Moved),
            entries.Count(e => e.Kind == CardChangeKind.Bucket));
    }

    /// <summary>Whether the server's agent may still read the board (it is a member).</summary>
    public static async Task<bool> CanSeeAsync(PrikBoardDbContext db, Guid agentId, Guid boardId, CancellationToken ct) =>
        await BoardAccess.RoleOnBoardAsync(db, boardId, agentId, ct) is not null;
}
