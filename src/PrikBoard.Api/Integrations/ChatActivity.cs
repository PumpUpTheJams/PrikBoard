using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// One line of a board's updates, as data: each chat app draws it its own way (<see cref="Embeds.UpdateLine"/>,
/// <see cref="SlackBlocks.UpdateLine"/>). Names are as people wrote them; the drawing escapes them.
/// </summary>
/// <param name="Kind">What happened: one of <see cref="ChatUpdates"/>.</param>
/// <param name="ReadableId">The card's readable id, e.g. PRIK-12.</param>
/// <param name="Path">The card's path in the web app.</param>
/// <param name="Title">The card's title.</param>
/// <param name="By">Who did it, or "someone".</param>
/// <param name="Into">NewCards: the bucket the card is in now, if any.</param>
/// <param name="From">Moves: the first bucket, null for loose on the board.</param>
/// <param name="To">Moves: the last bucket, null for loose on the board.</param>
/// <param name="People">Assignments: who was assigned.</param>
/// <param name="Started">AgentWork: true when an agent started, false when it stopped.</param>
public sealed record ChatUpdateLine(
    ChatUpdates Kind, string ReadableId, string Path, string Title, string By,
    string? Into = null, string? From = null, string? To = null, IReadOnlyList<string>? People = null, bool Started = false);

/// <summary>What a chat app's bot does for <see cref="ChatActivity"/>: bring a remembered preview up to date, and post updates.</summary>
public interface IChatActivityTarget
{
    ChatPlatform Platform { get; }

    /// <summary>The public address links are drawn with while the bot is online; null while it isn't, so nothing is posted.</summary>
    string? OnlineAt { get; }

    /// <summary>
    /// Shows a remembered message's previews as they are now (null where a source previews nothing any more); false when
    /// the message is gone, so it needn't be followed any longer.
    /// </summary>
    Task<bool> RefreshAsync(ChatPreviewMessage message, IReadOnlyList<LinkPreview?> previews, CancellationToken ct);

    /// <summary>Posts one board's updates in a channel.</summary>
    Task PostUpdatesAsync(string channelId, Board board, IReadOnlyList<ChatUpdateLine> lines, CancellationToken ct);
}

/// <summary>
/// What happens on boards, as the chat bots pass it on. It follows the card history (<see cref="CardChange"/>, which
/// every write path records) every few seconds, so there is one source of change and nothing to hook into handlers:
/// <list type="bullet">
/// <item>Messages with previews of changed cards (<see cref="ChatPreviewMessage"/>) are brought up to date, and say "no
/// longer available" once a card is gone or its board is disconnected from the channel (<see cref="RefreshBoard"/>).</item>
/// <item>Channels that follow a board's updates (<see cref="ChatChannel.Updates"/>) get one post per board with what
/// happened, gathered for <c>Chat:UpdatesDelaySeconds</c> (30) so a drag or a burst of edits is one post.</item>
/// </list>
/// Each chat app takes part through its <see cref="IChatActivityTarget"/>, while its bot is online. One instance only,
/// like the bots. Tests turn the timer off (<c>Chat:ActivitySeconds</c> 0) and call <see cref="TickAsync"/>; the older
/// <c>Discord:</c> names of both settings still work.
/// </summary>
public sealed class ChatActivity(
    IServiceScopeFactory scopes, IEnumerable<IChatActivityTarget> targets, IConfiguration config, TimeProvider time, ILogger<ChatActivity> logger)
    : BackgroundService
{
    /// <summary>How far back each look reaches before the last change seen, for saves that commit late.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly IReadOnlyList<IChatActivityTarget> _targets = targets.ToList();
    private readonly HashSet<Guid> _boardsToRefresh = [];
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly Dictionary<(ChatPlatform Platform, string ChannelId, Guid BoardId), PendingUpdates> _pending = [];
    private DateTimeOffset _watermark = time.GetUtcNow();
    private DateTimeOffset _purgedAt = DateTimeOffset.MinValue;

    private TimeSpan Delay => TimeSpan.FromSeconds(Setting("UpdatesDelaySeconds", 30));

    private int Setting(string name, int fallback) =>
        config.GetValue<int?>($"Chat:{name}") ?? config.GetValue($"Discord:{name}", fallback);

    /// <summary>The board's previews are looked at again on the next tick: it was disconnected from a workspace or channel.</summary>
    public void RefreshBoard(Guid boardId)
    {
        lock (_lock) _boardsToRefresh.Add(boardId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Setting("ActivitySeconds", 5);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Passing board activity on to the chat apps failed.");
            }
        }
    }

    /// <summary>Looks at the changes since the last tick, refreshes previews and posts the updates that waited long enough.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var online = _targets.Where(t => t.OnlineAt is not null).ToDictionary(t => t.Platform);
            // What waits for a bot that went offline isn't saved up for later.
            foreach (var key in _pending.Keys.Where(k => !online.ContainsKey(k.Platform)).ToList()) _pending.Remove(key);
            if (online.Count == 0)
            {
                _watermark = now;
                lock (_lock) _boardsToRefresh.Clear();
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();

            var since = _watermark - Overlap;
            var changes = await db.CardChanges.AsNoTracking().Where(c => c.At > since).OrderBy(c => c.At).ToListAsync(ct);
            // Quick edits are merged into the change before them, which then shows again with a later time.
            var newOnes = changes.Where(c => !_seen.ContainsKey(c.Id)).ToList();
            var changed = changes.Where(c => !_seen.TryGetValue(c.Id, out var at) || at != c.At).ToList();
            foreach (var change in changes) _seen[change.Id] = change.At;
            if (changes.Count > 0) _watermark = changes.Max(c => c.At);
            foreach (var old in _seen.Where(s => s.Value < since).Select(s => s.Key).ToList()) _seen.Remove(old);

            await RefreshPreviewsAsync(db, online, changed.Select(c => c.CardId).ToHashSet(), now, ct);
            await CollectUpdatesAsync(db, online, newOnes, now, ct);
            await PostUpdatesAsync(db, online, now, ct);

            if (now - _purgedAt > TimeSpan.FromHours(1))
            {
                var cutoff = now - ChatPreviewMessage.KeptFor;
                await db.ChatPreviewMessages.Where(m => m.PostedAt < cutoff).ExecuteDeleteAsync(ct);
                _purgedAt = now;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshPreviewsAsync(
        PrikBoardDbContext db, Dictionary<ChatPlatform, IChatActivityTarget> online, HashSet<Guid> cardIds, DateTimeOffset now, CancellationToken ct)
    {
        List<Guid> boardIds;
        lock (_lock)
        {
            boardIds = [.. _boardsToRefresh];
            _boardsToRefresh.Clear();
        }
        if (cardIds.Count == 0 && boardIds.Count == 0) return;

        var cutoff = now - ChatPreviewMessage.KeptFor;
        var cards = cardIds.ToList();
        var platforms = online.Keys.ToList();
        var messages = await db.ChatPreviewMessages
            .Where(m => platforms.Contains(m.Platform) && m.PostedAt > cutoff
                        && (m.CardIds.Any(id => cards.Contains(id)) || m.BoardIds.Any(id => boardIds.Contains(id))))
            .ToListAsync(ct);
        foreach (var message in messages)
        {
            var target = online[message.Platform];
            var previews = await ChatPreviews.RefreshAsync(db, message, target.OnlineAt!, ct);
            // Deleted in the chat app: nothing left to keep up to date.
            if (!await target.RefreshAsync(message, previews, ct)) db.ChatPreviewMessages.Remove(message);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds the changes that channels follow to what waits to be posted there.</summary>
    private async Task CollectUpdatesAsync(
        PrikBoardDbContext db, Dictionary<ChatPlatform, IChatActivityTarget> online, List<CardChange> changes, DateTimeOffset now, CancellationToken ct)
    {
        var events = changes.SelectMany(c => c.Entries.Select(e => Event(c, e))).OfType<Update>().ToList();
        if (events.Count == 0) return;

        var boardIds = events.Select(e => e.BoardId).Distinct().ToList();
        var platforms = online.Keys.ToList();
        var connections = await (
                from limit in db.ChatBoardChannels
                where platforms.Contains(limit.Platform) && boardIds.Contains(limit.BoardId)
                join workspace in db.ChatWorkspaces on new { limit.Platform, limit.WorkspaceId } equals new { workspace.Platform, workspace.WorkspaceId }
                join member in db.BoardMembers on new { limit.BoardId, PrincipalId = workspace.AgentId } equals new { member.BoardId, member.PrincipalId }
                select limit)
            .AsNoTracking().ToListAsync(ct);
        foreach (var limit in connections)
        {
            foreach (var channel in limit.Channels.Where(c => c.Updates != ChatUpdates.None))
            {
                var follows = events.Where(e => e.BoardId == limit.BoardId && channel.Updates.HasFlag(e.Kind)).ToList();
                if (follows.Count == 0) continue;
                var key = (limit.Platform, channel.Id, limit.BoardId);
                if (!_pending.TryGetValue(key, out var pending)) _pending[key] = pending = new PendingUpdates(now);
                pending.Events.AddRange(follows);
            }
        }
    }

    /// <summary>The update an entry of a change is, if channels can follow it.</summary>
    private static Update? Event(CardChange change, CardChangeEntry entry) => entry.Kind switch
    {
        CardChangeKind.Created or CardChangeKind.Copied or CardChangeKind.Moved or CardChangeKind.Restored =>
            new Update(ChatUpdates.NewCards, change.BoardId, change.CardId, change.ById, change.At),
        CardChangeKind.Bucket => new Update(ChatUpdates.Moves, change.BoardId, change.CardId, change.ById, change.At, entry.Before, entry.After),
        CardChangeKind.Assignees when entry.Added is { Count: > 0 } added =>
            new Update(ChatUpdates.Assignments, change.BoardId, change.CardId, change.ById, change.At, People: added),
        CardChangeKind.AgentWorking => new Update(ChatUpdates.AgentWork, change.BoardId, change.CardId, change.ById, change.At, After: entry.After),
        _ => null,
    };

    private async Task PostUpdatesAsync(PrikBoardDbContext db, Dictionary<ChatPlatform, IChatActivityTarget> online, DateTimeOffset now, CancellationToken ct)
    {
        var due = _pending.Where(p => now - p.Value.Since >= Delay).ToList();
        foreach (var ((platform, channelId, boardId), pending) in due)
        {
            _pending.Remove((platform, channelId, boardId));
            var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct);
            if (board is null) continue;
            var lines = await LinesAsync(db, board, pending.Events, ct);
            if (lines.Count == 0) continue;
            await online[platform].PostUpdatesAsync(channelId, board, lines, ct);
        }
    }

    /// <summary>One line per card, in the order things happened; moves of the same card become one from the first bucket to the last.</summary>
    private static async Task<List<ChatUpdateLine>> LinesAsync(PrikBoardDbContext db, Board board, List<Update> events, CancellationToken ct)
    {
        var cardIds = events.Select(e => e.CardId).Distinct().ToList();
        var cards = await db.Cards.AsNoTracking().Where(c => cardIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Number, c.Title, c.BucketId }).ToDictionaryAsync(c => c.Id, ct);
        var bucketIds = cards.Values.Select(c => c.BucketId).OfType<Guid>().Distinct().ToList();
        var buckets = await db.Buckets.AsNoTracking().Where(b => bucketIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var people = events.Select(e => e.ById).OfType<Guid>()
            .Concat(events.SelectMany(e => e.People ?? []).Select(p => Guid.TryParse(p, out var id) ? id : Guid.Empty))
            .Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => people.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ShownName, ct);
        string Name(Guid? id) => id is { } known && names.TryGetValue(known, out var name) ? name : "someone";

        var lines = new List<ChatUpdateLine>();
        foreach (var group in events.OrderBy(e => e.At).GroupBy(e => (e.CardId, e.Kind)))
        {
            // A card deleted since is left out.
            if (!cards.TryGetValue(group.Key.CardId, out var card)) continue;
            var line = new ChatUpdateLine(group.Key.Kind, CardLinks.ReadableId(board.Key, card.Number), CardLinks.CardPath(board.ShortId, card.Number),
                card.Title, Name(group.Last().ById));
            var first = group.First();
            var last = group.Last();
            switch (group.Key.Kind)
            {
                case ChatUpdates.NewCards:
                    lines.Add(line with
                    {
                        By = Name(first.ById),
                        Into = card.BucketId is { } bucketId && buckets.TryGetValue(bucketId, out var into) ? into : null,
                    });
                    break;
                case ChatUpdates.Moves:
                    if (first.Before == last.After) break;
                    lines.Add(line with { From = first.Before, To = last.After });
                    break;
                case ChatUpdates.Assignments:
                    lines.Add(line with
                    {
                        People = group.SelectMany(e => e.People ?? []).Distinct().Select(p => Guid.TryParse(p, out var id) ? Name(id) : "someone").ToList(),
                    });
                    break;
                case ChatUpdates.AgentWork:
                    lines.Add(line with { Started = last.After == "true" });
                    break;
            }
        }
        return lines;
    }

    /// <summary>A change a channel can follow.</summary>
    private sealed record Update(
        ChatUpdates Kind, Guid BoardId, Guid CardId, Guid? ById, DateTimeOffset At,
        string? Before = null, string? After = null, IReadOnlyList<string>? People = null);

    private sealed class PendingUpdates(DateTimeOffset since)
    {
        public DateTimeOffset Since { get; } = since;
        public List<Update> Events { get; } = [];
    }
}
