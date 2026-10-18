using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

// Small value objects stored inside a note (as JSON columns), always read and replaced as a whole list.

/// <summary>Whether an assignee is a person or an AI agent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssigneeKind>))]
public enum AssigneeKind
{
    Person,
    Agent,
}

public class AssigneeEntry
{
    public required string Name { get; set; }
    public AssigneeKind Kind { get; set; }
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
