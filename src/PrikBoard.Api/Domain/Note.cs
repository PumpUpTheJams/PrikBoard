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
    public string? Body { get; set; }
    public NoteColor Color { get; set; } = NoteColor.Butter;
    /// <summary>Lower-case labels, see <see cref="NoteTags.Normalize(string)"/>.</summary>
    public List<string> Tags { get; set; } = [];
    public List<AssigneeEntry> Assignees { get; set; } = [];
    public List<ChecklistEntry> Checklist { get; set; } = [];
    public List<LinkEntry> Links { get; set; } = [];
    /// <summary>Set while an AI agent is actively working on the note; the board shows a halo.</summary>
    public bool AgentWorking { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
