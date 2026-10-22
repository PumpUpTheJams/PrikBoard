using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

// Small value objects stored in a card's components (its jsonb column), and the kind of an assignee.

/// <summary>Whether an assignee is a person or an AI agent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssigneeKind>))]
public enum AssigneeKind
{
    Person,
    Agent,
}

public class ChecklistEntry
{
    public required string Text { get; set; }
    public bool Done { get; set; }
}

public class LinkEntry
{
    public required string Title { get; set; }
    public required string Url { get; set; }
}
