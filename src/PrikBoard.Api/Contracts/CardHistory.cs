using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// One change to a card: who made it, when, and what changed. Quick edits of a title, color or component by the same caller
/// within two minutes are merged into one change. Moving a card within its bucket, and moving or resizing its components,
/// images and drawings, is not recorded.
/// </summary>
/// <param name="Id">Change identifier.</param>
/// <param name="At">When it happened (UTC); for merged edits, the last of them.</param>
/// <param name="By">The person or agent who made it, with their current name; null once they no longer exist.</param>
/// <param name="Entries">What changed, one entry per field, component, image or drawing.</param>
public record CardChangeDetail(Guid Id, DateTimeOffset At, Assignee? By, IReadOnlyList<CardChangeEntryDetail> Entries)
{
    /// <summary>Maps a change; <paramref name="principals"/> holds its author and the people in assignee entries.</summary>
    public static CardChangeDetail From(CardChange change, IReadOnlyDictionary<Guid, Principal> principals) =>
        new(change.Id, change.At,
            change.ById is { } by ? Assignee.Of(by, principals) : null,
            change.Entries.Select(e => CardChangeEntryDetail.From(e, principals)).ToList());

    /// <summary>The principals a list of changes mentions: their authors and the assignees added or removed.</summary>
    public static IEnumerable<Guid> PrincipalIds(CardChange change) =>
        change.Entries.Where(e => e.Kind == CardChangeKind.Assignees)
            .SelectMany(e => (e.Added ?? []).Concat(e.Removed ?? []))
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Append(change.ById ?? Guid.Empty)
            .Where(id => id != Guid.Empty);
}

/// <summary>One thing that changed. Which fields are set depends on kind.</summary>
/// <param name="Kind">What changed: Created, Title, Color, Tags, Assignees, AgentWorking, Bucket, ComponentAdded, ComponentChanged, ComponentRemoved, ImageAdded, ImageRemoved, DrawingAdded, DrawingRemoved, Deleted, Restored, Copied (added as a copy of another card), Moved (added as the new place of a card cut from another board), MovedAway (cut and pasted on another board), or Other for a change this version cannot describe. Treat kinds you do not know like Other.</param>
/// <param name="Before">The old value: the old title or color, the name of the bucket it left (null: loose on the board), a component's kind, a removed image's caption or file name, or for Copied and Moved the original card's readable id (for MovedAway, the new card's).</param>
/// <param name="After">The new value: the new title or color, the name of the bucket it went into (null: loose), a component's title, an added image's caption or file name, "true"/"false" for AgentWorking, or for Copied, Moved and MovedAway the other card's link.</param>
/// <param name="Added">Tags added, or the names of the people and agents assigned.</param>
/// <param name="Removed">Tags removed, or the names of the people and agents unassigned.</param>
public record CardChangeEntryDetail(
    CardChangeKind Kind,
    string? Before,
    string? After,
    IReadOnlyList<string>? Added,
    IReadOnlyList<string>? Removed)
{
    public static CardChangeEntryDetail From(CardChangeEntry entry, IReadOnlyDictionary<Guid, Principal> principals)
    {
        IReadOnlyList<string>? Names(List<string>? ids) =>
            ids?.Select(id => Guid.TryParse(id, out var parsed) ? Assignee.Of(parsed, principals).Name : id).ToList();

        return entry.Kind == CardChangeKind.Assignees
            ? new(entry.Kind, entry.Before, entry.After, Names(entry.Added), Names(entry.Removed))
            : new(entry.Kind, entry.Before, entry.After, entry.Added, entry.Removed);
    }
}
