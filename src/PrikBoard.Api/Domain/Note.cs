namespace PrikBoard.Api.Domain;

/// <summary>A post-it note: one unit of work stuck somewhere on a board, inside a bucket or loose.</summary>
public class Note
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    /// <summary>The bucket containing the note's centre, or null when it is loose. Maintained by <see cref="Layout"/>.</summary>
    public Guid? BucketId { get; set; }
    public required string Title { get; set; }
    public NoteColor Color { get; set; } = NoteColor.Butter;
    /// <summary>Names of the board's tags (<see cref="BoardTag"/>) on this note, see <see cref="NoteTags.Normalize(string)"/>.</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>
    /// Principal ids of the people and agents on the note, in display order. Added ones must be able to reach the board
    /// (<see cref="Auth.BoardAccess.AssignableIds"/>); ones who later lose access stay.
    /// </summary>
    public List<Guid> AssigneeIds { get; set; } = [];
    /// <summary>The note's content (text, checklists, links) laid out on its own canvas; see <see cref="NoteBlock"/>.</summary>
    public List<NoteBlock> Blocks { get; set; } = [];
    /// <summary>Set while an AI agent is actively working on the note; the board shows a halo.</summary>
    public bool AgentWorking { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>The user or agent that stuck the note on the board; null for older notes, or once they no longer exist.</summary>
    public Guid? CreatedById { get; set; }
    public Principal? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was deleted; it can be restored until <see cref="Data.Tombstones.Retention"/> has passed. Hidden by the query filter while set.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
