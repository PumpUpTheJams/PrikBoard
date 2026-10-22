using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// One save that changed a card, by one person or agent: what the history in the expanded card shows. Recorded by
/// <see cref="Data.CardHistory"/>; quick edits of the same things by the same caller are merged into one.
/// </summary>
public class CardChange
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CardId { get; set; }
    public Guid BoardId { get; set; }
    /// <summary>Who made the change; null once they no longer exist.</summary>
    public Guid? ById { get; set; }
    public Principal? By { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>What changed, stored as one JSON column.</summary>
    public List<CardChangeEntry> Entries { get; set; } = [];
}

/// <summary>What one entry in a card's history is about.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardChangeKind>))]
public enum CardChangeKind
{
    /// <summary>The card was added to the board.</summary>
    Created,
    /// <summary>The title changed from before to after.</summary>
    Title,
    /// <summary>The color changed from before to after.</summary>
    Color,
    /// <summary>Tags were added and/or removed.</summary>
    Tags,
    /// <summary>People or agents were assigned (added) and/or unassigned (removed).</summary>
    Assignees,
    /// <summary>The agent-working halo went on (after "true") or off (after "false").</summary>
    AgentWorking,
    /// <summary>The card moved from one bucket (before) to another (after); null is loose on the board.</summary>
    Bucket,
    /// <summary>A component was added; before is its kind (Text, Checklist or Links), after its title (null without one).</summary>
    ComponentAdded,
    /// <summary>A component's content changed; before is its kind, after its title (null without one).</summary>
    ComponentChanged,
    /// <summary>A component was removed; before is its kind, after its title (null without one).</summary>
    ComponentRemoved,
    /// <summary>An image was attached; after is its caption or file name.</summary>
    ImageAdded,
    /// <summary>An image was taken off or deleted; before is its caption or file name.</summary>
    ImageRemoved,
    /// <summary>A drawing was added to the card's canvas.</summary>
    DrawingAdded,
    /// <summary>A drawing was taken off or deleted.</summary>
    DrawingRemoved,
    /// <summary>The card was deleted.</summary>
    Deleted,
    /// <summary>The card was restored from the trash.</summary>
    Restored,
    /// <summary>
    /// The card was added as a copy of another (pasteItems); before is that card's readable id, after its link: a path on
    /// this server, or a full URL when it was copied from another PrikBoard server.
    /// </summary>
    Copied,
    /// <summary>A change this version of PrikBoard can't describe (recorded by another version); its other fields may be set.</summary>
    Other,
}

/// <summary>One thing that changed in a <see cref="CardChange"/>; only the fields that fit <see cref="Kind"/> are set.</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Before">The old value (see <see cref="CardChangeKind"/>).</param>
/// <param name="After">The new value (see <see cref="CardChangeKind"/>).</param>
/// <param name="Added">Tag names, or principal ids of people and agents, that were added.</param>
/// <param name="Removed">Tag names, or principal ids, that were removed.</param>
/// <param name="Ref">The component, image or drawing it is about, so later edits of the same one can be merged.</param>
public record CardChangeEntry(
    CardChangeKind Kind,
    string? Before = null,
    string? After = null,
    List<string>? Added = null,
    List<string>? Removed = null,
    Guid? Ref = null);
