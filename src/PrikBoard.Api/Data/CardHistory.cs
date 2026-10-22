using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Writes each card's history (<see cref="CardChange"/>) as part of the save that changes it, by comparing the tracked
/// cards and the images and drawings on them with what was loaded. Every write path (REST, MCP, undo) goes through here, so handlers
/// don't record anything themselves, except where they bypass the change tracker (<see cref="Record"/>). Only saves made
/// for a signed-in caller are recorded, and not cards added together with their board (an import).
/// </summary>
public sealed class CardHistory(IHttpContextAccessor accessor) : SaveChangesInterceptor
{
    /// <summary>Quick edits of a title, color or component by the same caller within this time are merged into one change.</summary>
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(2);

    private static readonly CardChangeKind[] Mergeable = [CardChangeKind.Title, CardChangeKind.Color, CardChangeKind.ComponentChanged];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PrikBoardDbContext db && Caller() is { } actor) await RecordAsync(db, actor, true, cancellationToken);
        return result;
    }

    // Handlers save asynchronously, but a synchronous save must not slip past the history either. With async false,
    // RecordAsync runs its queries synchronously, so it completes before returning.
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is PrikBoardDbContext db && Caller() is { } actor) RecordAsync(db, actor, false, default).GetAwaiter().GetResult();
        return result;
    }

    private Guid? Caller() =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user.PrincipalId() : null;

    /// <summary>Adds a change made outside the change tracker (ExecuteUpdate); it is written with the next save.</summary>
    public static void Record(PrikBoardDbContext db, ClaimsPrincipal principal, Guid cardId, Guid boardId, CardChangeEntry entry) =>
        db.CardChanges.Add(new CardChange { CardId = cardId, BoardId = boardId, ById = principal.PrincipalId(), Entries = [entry] });

    private static async Task RecordAsync(PrikBoardDbContext db, Guid actor, bool async, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries<Board>().Any(e => e.State == EntityState.Added)) return;

        var changes = new Dictionary<Guid, (Guid BoardId, List<CardChangeEntry> Entries)>();
        void Add(Guid cardId, Guid boardId, CardChangeEntry entry)
        {
            if (!changes.TryGetValue(cardId, out var change)) changes[cardId] = change = (boardId, []);
            change.Entries.Add(entry);
        }

        // A card added with a change of its own (a pasted copy says where it came from) needs no Created entry.
        var described = db.ChangeTracker.Entries<CardChange>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.CardId).ToHashSet();
        var created = new HashSet<Guid>();
        var moves = new List<(Guid CardId, Guid BoardId, Guid? From, Guid? To)>();
        foreach (var entry in db.ChangeTracker.Entries<Card>())
        {
            var card = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                created.Add(card.Id);
                if (!described.Contains(card.Id)) Add(card.Id, card.BoardId, new(CardChangeKind.Created));
                continue;
            }
            if (entry.State != EntityState.Modified) continue;

            T Was<T>(System.Linq.Expressions.Expression<Func<Card, T>> property) => entry.Property(property).OriginalValue;
            if (Was(n => n.DeletedAt) is not null && card.DeletedAt is null) Add(card.Id, card.BoardId, new(CardChangeKind.Restored));
            if (Was(n => n.Title) is var title && title != card.Title)
            {
                Add(card.Id, card.BoardId, new(CardChangeKind.Title, title, card.Title));
            }
            if (Was(n => n.Color) is var color && color != card.Color)
            {
                Add(card.Id, card.BoardId, new(CardChangeKind.Color, color.ToString(), card.Color.ToString()));
            }
            if (Diff(Was(n => n.Tags), card.Tags) is { } tags) Add(card.Id, card.BoardId, tags with { Kind = CardChangeKind.Tags });
            if (Diff(Was(n => n.AssigneeIds).Select(id => id.ToString()), card.AssigneeIds.Select(id => id.ToString())) is { } people)
            {
                Add(card.Id, card.BoardId, people with { Kind = CardChangeKind.Assignees });
            }
            if (Was(n => n.AgentWorking) != card.AgentWorking)
            {
                Add(card.Id, card.BoardId, new(CardChangeKind.AgentWorking, After: card.AgentWorking ? "true" : "false"));
            }
            if (Was(n => n.BucketId) is var bucket && bucket != card.BucketId) moves.Add((card.Id, card.BoardId, bucket, card.BucketId));
            foreach (var component in DiffComponents(Was(n => n.Components), card.Components)) Add(card.Id, card.BoardId, component);
        }

        // Images and drawings on a card's canvas: added, moved onto or off the card, deleted (ExecuteUpdate, see Record) or restored.
        void Attachments<T>(IEnumerable<EntityEntry<T>> entries, Func<T, string?> label, CardChangeKind added, CardChangeKind removed)
            where T : class
        {
            foreach (var entry in entries)
            {
                var (id, boardId) = ((Guid)entry.Property("Id").CurrentValue!, (Guid)entry.Property("BoardId").CurrentValue!);
                var on = (Guid?)entry.Property("CardId").CurrentValue;
                if (entry.State == EntityState.Added)
                {
                    // What comes with a card as it is added (a pasted copy's images) is part of adding it.
                    if (on is { } cardId && !created.Contains(cardId)) Add(cardId, boardId, new(added, After: label(entry.Entity), Ref: id));
                    continue;
                }
                if (entry.State != EntityState.Modified) continue;

                var was = (Guid?)entry.Property("CardId").OriginalValue;
                var restored = entry.Property("DeletedAt").OriginalValue is not null && entry.Property("DeletedAt").CurrentValue is null;
                if (was is { } from && from != on) Add(from, boardId, new(removed, Before: label(entry.Entity), Ref: id));
                if (on is { } to && (to != was || restored)) Add(to, boardId, new(added, After: label(entry.Entity), Ref: id));
            }
        }
        Attachments(db.ChangeTracker.Entries<BoardImage>(), i => i.Caption ?? i.File?.FileName, CardChangeKind.ImageAdded, CardChangeKind.ImageRemoved);
        Attachments(db.ChangeTracker.Entries<BoardDrawing>(), _ => null, CardChangeKind.DrawingAdded, CardChangeKind.DrawingRemoved);

        if (moves.Count > 0)
        {
            var names = await BucketNamesAsync(db, moves.SelectMany(m => new[] { m.From, m.To }).OfType<Guid>().ToHashSet(), async, ct);
            foreach (var (cardId, boardId, from, to) in moves)
            {
                Add(cardId, boardId, new(CardChangeKind.Bucket, from is { } f ? names.GetValueOrDefault(f) : null, to is { } t ? names.GetValueOrDefault(t) : null));
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (cardId, (boardId, entries)) in changes)
        {
            if (entries.All(e => Mergeable.Contains(e.Kind)) && await LatestAsync(db, cardId, async, ct) is { } last
                && last.ById == actor && now - last.At < MergeWithin)
            {
                // A title or color given right after adding the card is part of creating it.
                if (last.Entries is [{ Kind: CardChangeKind.Created }]
                    && entries.All(e => e.Kind is CardChangeKind.Title or CardChangeKind.Color))
                {
                    continue;
                }
                if (last.Entries.All(e => Mergeable.Contains(e.Kind)))
                {
                    var merged = Merge(last.Entries, entries);
                    if (merged.Count == 0)
                    {
                        db.CardChanges.Remove(last);
                    }
                    else
                    {
                        last.Entries = merged;
                        last.At = now;
                    }
                    continue;
                }
            }
            db.CardChanges.Add(new CardChange { CardId = cardId, BoardId = boardId, ById = actor, At = now, Entries = entries });
        }
    }

    /// <summary>What was added to and removed from a list, or null when nothing was.</summary>
    private static CardChangeEntry? Diff(IEnumerable<string> before, IEnumerable<string> after)
    {
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        return added.Count == 0 && removed.Count == 0
            ? null
            : new(CardChangeKind.Tags, Added: added.Count > 0 ? added : null, Removed: removed.Count > 0 ? removed : null);
    }

    /// <summary>Components added, removed or with new content; moving or resizing a component isn't a change worth listing.</summary>
    private static IEnumerable<CardChangeEntry> DiffComponents(List<CardComponent> before, List<CardComponent> after)
    {
        var old = before.ToDictionary(b => b.Id);
        foreach (var component in after)
        {
            if (!old.Remove(component.Id, out var was))
            {
                yield return new(CardChangeKind.ComponentAdded, component.Kind.ToString(), component.Title, Ref: component.Id);
            }
            else if (Content(was) != Content(component))
            {
                yield return new(CardChangeKind.ComponentChanged, component.Kind.ToString(), component.Title, Ref: component.Id);
            }
        }
        foreach (var removed in old.Values)
        {
            yield return new(CardChangeKind.ComponentRemoved, removed.Kind.ToString(), removed.Title, Ref: removed.Id);
        }
    }

    private static string Content(CardComponent component) => JsonSerializer.Serialize(new { component.Title, component.Text, component.Items, component.Links });

    /// <summary>Folds <paramref name="next"/> into <paramref name="entries"/>, dropping titles and colors that are back where they started.</summary>
    private static List<CardChangeEntry> Merge(List<CardChangeEntry> entries, List<CardChangeEntry> next)
    {
        var merged = entries.ToList();
        foreach (var entry in next)
        {
            var i = merged.FindIndex(e => e.Kind == entry.Kind && e.Ref == entry.Ref);
            if (i < 0) merged.Add(entry);
            else merged[i] = merged[i] with { After = entry.After };
        }
        merged.RemoveAll(e => e.Kind is CardChangeKind.Title or CardChangeKind.Color && e.Before == e.After);
        return merged;
    }

    private static async Task<CardChange?> LatestAsync(PrikBoardDbContext db, Guid cardId, bool async, CancellationToken ct)
    {
        var latest = db.CardChanges.Where(c => c.CardId == cardId).OrderByDescending(c => c.At).ThenByDescending(c => c.Id);
        return async ? await latest.FirstOrDefaultAsync(ct) : latest.FirstOrDefault();
    }

    /// <summary>Bucket names as they are now (a bucket's name at the time of the move is what the history keeps).</summary>
    private static async Task<Dictionary<Guid, string>> BucketNamesAsync(
        PrikBoardDbContext db, HashSet<Guid> ids, bool async, CancellationToken ct)
    {
        var names = db.ChangeTracker.Entries<Bucket>().Where(e => ids.Contains(e.Entity.Id)).ToDictionary(e => e.Entity.Id, e => e.Entity.Name);
        var missing = ids.Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var query = db.Buckets.AsNoTracking().IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
                .Where(b => missing.Contains(b.Id)).Select(b => new { b.Id, b.Name });
            foreach (var bucket in async ? await query.ToListAsync(ct) : query.ToList())
            {
                names[bucket.Id] = bucket.Name;
            }
        }
        return names;
    }
}

/// <summary>Stores a <see cref="CardChange"/>'s entries as one jsonb array with camelCase names, like <see cref="CardComponentsJson"/>.</summary>
public static class CardChangesJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Beats the enum's own converter, so a kind this version doesn't know reads as Other instead of failing the row.
        Converters = { new KindConverter() },
    };

    private sealed class KindConverter : System.Text.Json.Serialization.JsonConverter<CardChangeKind>
    {
        public override CardChangeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Enum.TryParse<CardChangeKind>(reader.GetString(), out var kind) && Enum.IsDefined(kind)
                ? kind
                : CardChangeKind.Other;

        public override void Write(Utf8JsonWriter writer, CardChangeKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private static string Write(List<CardChangeEntry> entries) => JsonSerializer.Serialize(entries, Options);

    private static List<CardChangeEntry> Read(string json) => JsonSerializer.Deserialize<List<CardChangeEntry>>(json, Options) ?? [];

    public static readonly ValueConverter<List<CardChangeEntry>, string> Converter = new(v => Write(v), v => Read(v));

    public static readonly ValueComparer<List<CardChangeEntry>> Comparer = new(
        (a, b) => Write(a!) == Write(b!),
        v => Write(v).GetHashCode(),
        v => Read(Write(v)));
}
