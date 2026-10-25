using Discord;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// What happens on boards, as the Discord bot passes it on. It follows the card history (<see cref="CardChange"/>, which
/// every write path records) every few seconds, so there is one source of change and nothing to hook into handlers:
/// <list type="bullet">
/// <item>Replies with previews of changed cards (<see cref="ChatPreviewMessage"/>) are edited to match, and say "no
/// longer available" once a card is gone or its board is disconnected from the channel (<see cref="RefreshBoard"/>).</item>
/// <item>Channels that follow a board's updates (<see cref="ChatChannel.Updates"/>) get one post per board with what
/// happened, gathered for <c>Discord:UpdatesDelaySeconds</c> (30) so a drag or a burst of edits is one post.</item>
/// </list>
/// One instance only, like the bot itself. Tests turn the timer off (<c>Discord:ActivitySeconds</c> 0) and call <see cref="TickAsync"/>.
/// </summary>
public sealed class DiscordActivity(
    IServiceScopeFactory scopes, IDiscordGateway gateway, DiscordBot bot, IConfiguration config, TimeProvider time, ILogger<DiscordActivity> logger)
    : BackgroundService
{
    /// <summary>How far back each look reaches before the last change seen, for saves that commit late.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _boardsToRefresh = [];
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly Dictionary<(string ChannelId, Guid BoardId), PendingUpdates> _pending = [];
    private DateTimeOffset _watermark = time.GetUtcNow();
    private DateTimeOffset _purgedAt = DateTimeOffset.MinValue;

    private TimeSpan Delay => TimeSpan.FromSeconds(config.GetValue("Discord:UpdatesDelaySeconds", 30));

    /// <summary>The board's previews are looked at again on the next tick: it was disconnected from a server or channel.</summary>
    public void RefreshBoard(Guid boardId)
    {
        lock (_lock) _boardsToRefresh.Add(boardId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = config.GetValue("Discord:ActivitySeconds", 5);
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
                logger.LogError(error, "Passing board activity on to Discord failed.");
            }
        }
    }

    /// <summary>Looks at the changes since the last tick, edits previews and posts the updates that waited long enough.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (gateway.State != DiscordBotState.Online || bot.Current.PublicUrl is not { } site)
            {
                // Nothing to post to: what happens meanwhile isn't saved up for later.
                _watermark = now;
                lock (_lock) _boardsToRefresh.Clear();
                _pending.Clear();
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var embeds = new Embeds(site);

            var since = _watermark - Overlap;
            var changes = await db.CardChanges.AsNoTracking().Where(c => c.At > since).OrderBy(c => c.At).ToListAsync(ct);
            // Quick edits are merged into the change before them, which then shows again with a later time.
            var newOnes = changes.Where(c => !_seen.ContainsKey(c.Id)).ToList();
            var changed = changes.Where(c => !_seen.TryGetValue(c.Id, out var at) || at != c.At).ToList();
            foreach (var change in changes) _seen[change.Id] = change.At;
            if (changes.Count > 0) _watermark = changes.Max(c => c.At);
            foreach (var old in _seen.Where(s => s.Value < since).Select(s => s.Key).ToList()) _seen.Remove(old);

            await RefreshPreviewsAsync(db, changed.Select(c => c.CardId).ToHashSet(), site, embeds, now, ct);
            await CollectUpdatesAsync(db, newOnes, now, ct);
            await PostUpdatesAsync(db, embeds, now, ct);

            if (now - _purgedAt > TimeSpan.FromHours(1))
            {
                var cutoff = now - ChatPreviewMessage.KeptFor;
                await db.ChatPreviewMessages.Where(m => m.Platform == ChatPlatform.Discord && m.PostedAt < cutoff).ExecuteDeleteAsync(ct);
                _purgedAt = now;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshPreviewsAsync(
        PrikBoardDbContext db, HashSet<Guid> cardIds, string site, Embeds embeds, DateTimeOffset now, CancellationToken ct)
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
        var messages = await db.ChatPreviewMessages
            .Where(m => m.Platform == ChatPlatform.Discord && m.PostedAt > cutoff && (m.CardIds.Any(id => cards.Contains(id)) || m.BoardIds.Any(id => boardIds.Contains(id))))
            .ToListAsync(ct);
        foreach (var message in messages)
        {
            var previews = await ChatPreviews.RefreshAsync(db, message, site, ct);
            var shown = previews.Select((p, i) => p is null ? embeds.Unavailable() : embeds.For(p, message.Sources[i])).ToList();
            if (!await gateway.EditAsync(message.ChannelId, message.MessageId, shown))
            {
                // Deleted in Discord: nothing left to keep up to date.
                db.ChatPreviewMessages.Remove(message);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds the changes that channels follow to what waits to be posted there.</summary>
    private async Task CollectUpdatesAsync(PrikBoardDbContext db, List<CardChange> changes, DateTimeOffset now, CancellationToken ct)
    {
        var events = changes.SelectMany(c => c.Entries.Select(e => Event(c, e))).OfType<Update>().ToList();
        if (events.Count == 0) return;

        var boardIds = events.Select(e => e.BoardId).Distinct().ToList();
        var connections = await (
                from limit in db.ChatBoardChannels
                where limit.Platform == ChatPlatform.Discord && boardIds.Contains(limit.BoardId)
                join guild in db.ChatWorkspaces on new { limit.Platform, limit.WorkspaceId } equals new { guild.Platform, guild.WorkspaceId }
                join member in db.BoardMembers on new { limit.BoardId, PrincipalId = guild.AgentId } equals new { member.BoardId, member.PrincipalId }
                select limit)
            .AsNoTracking().ToListAsync(ct);
        foreach (var limit in connections)
        {
            foreach (var channel in limit.Channels.Where(c => c.Updates != ChatUpdates.None))
            {
                var follows = events.Where(e => e.BoardId == limit.BoardId && channel.Updates.HasFlag(e.Kind)).ToList();
                if (follows.Count == 0) continue;
                if (!_pending.TryGetValue((channel.Id, limit.BoardId), out var pending))
                {
                    _pending[(channel.Id, limit.BoardId)] = pending = new PendingUpdates(now);
                }
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

    private async Task PostUpdatesAsync(PrikBoardDbContext db, Embeds embeds, DateTimeOffset now, CancellationToken ct)
    {
        var due = _pending.Where(p => now - p.Value.Since >= Delay).ToList();
        foreach (var ((channelId, boardId), pending) in due)
        {
            _pending.Remove((channelId, boardId));
            var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct);
            if (board is null) continue;
            var lines = await LinesAsync(db, board, embeds, pending.Events, ct);
            if (lines.Count == 0) continue;
            await gateway.PostAsync(channelId, [embeds.Updates(board.Name, CardLinks.BoardPath(board.ShortId), lines)]);
        }
    }

    /// <summary>One line per card, in the order things happened; moves of the same card become one from the first bucket to the last.</summary>
    private static async Task<List<string>> LinesAsync(PrikBoardDbContext db, Board board, Embeds embeds, List<Update> events, CancellationToken ct)
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
        string Name(Guid? id) => id is { } known && names.TryGetValue(known, out var name) ? Format.Sanitize(name) : "someone";

        var lines = new List<string>();
        foreach (var group in events.OrderBy(e => e.At).GroupBy(e => (e.CardId, e.Kind)))
        {
            // A card deleted since is left out.
            if (!cards.TryGetValue(group.Key.CardId, out var card)) continue;
            var link = embeds.CardLine(CardLinks.ReadableId(board.Key, card.Number), CardLinks.CardPath(board.ShortId, card.Number), card.Title);
            var first = group.First();
            var last = group.Last();
            switch (group.Key.Kind)
            {
                case ChatUpdates.NewCards:
                    var into = card.BucketId is { } bucketId && buckets.TryGetValue(bucketId, out var bucketName) ? $" to **{Format.Sanitize(bucketName)}**" : "";
                    lines.Add($"🆕 {link} added{into} by {Name(first.ById)}");
                    break;
                case ChatUpdates.Moves:
                    if (first.Before == last.After) break;
                    lines.Add($"➡️ {link} {Bucket(first.Before)} → **{Bucket(last.After)}** · {Name(last.ById)}");
                    break;
                case ChatUpdates.Assignments:
                    var assigned = group.SelectMany(e => e.People ?? []).Distinct()
                        .Select(p => Guid.TryParse(p, out var id) ? Name(id) : "someone").ToList();
                    lines.Add($"👤 {link} assigned to {string.Join(", ", assigned)} by {Name(last.ById)}");
                    break;
                case ChatUpdates.AgentWork:
                    lines.Add(last.After == "true"
                        ? $"🤖 {Name(last.ById)} started working on {link}"
                        : $"✅ {Name(last.ById)} stopped working on {link}");
                    break;
            }
        }
        return lines;

        static string Bucket(string? name) => name is null ? "loose" : Format.Sanitize(name);
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
