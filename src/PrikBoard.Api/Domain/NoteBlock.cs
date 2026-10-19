using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>What a <see cref="NoteBlock"/> holds. Serialized as a string so API clients can use readable names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteBlockKind>))]
public enum NoteBlockKind
{
    /// <summary>Free text, such as a description or notes.</summary>
    Text,
    /// <summary>A checklist, such as acceptance criteria.</summary>
    Checklist,
    /// <summary>A list of links, such as pull requests or documents.</summary>
    Links,
}

/// <summary>
/// One piece of content on a note's own canvas, shown when the note is expanded. A note can have any mix of blocks;
/// each sits at <see cref="X"/>, <see cref="Y"/> in note pixels (from the top-left of that canvas) and can be moved and
/// resized there. Only the fields that fit <see cref="Kind"/> are used. Stored with the note as one JSON column.
/// </summary>
public class NoteBlock
{
    public const int MaxBlocks = 30;
    public const int MaxTitleLength = 100;
    public const int MaxTextLength = 4000;
    public const int MaxItems = 50;
    public const int MaxLinks = 20;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public NoteBlockKind Kind { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Optional heading, e.g. "Acceptance criteria".</summary>
    public string? Title { get; set; }
    /// <summary>The text of a Text block.</summary>
    public string? Text { get; set; }
    /// <summary>The items of a Checklist block.</summary>
    public List<ChecklistEntry> Items { get; set; } = [];
    /// <summary>The links of a Links block.</summary>
    public List<LinkEntry> Links { get; set; } = [];

    /// <summary>Every piece of text in the block, for search.</summary>
    public IEnumerable<string> SearchText()
    {
        if (Title is not null) yield return Title;
        if (Text is not null) yield return Text;
        foreach (var item in Items) yield return item.Text;
        foreach (var link in Links)
        {
            yield return link.Title;
            yield return link.Url;
        }
    }
}
