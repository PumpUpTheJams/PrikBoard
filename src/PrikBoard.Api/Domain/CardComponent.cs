using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>What a <see cref="CardComponent"/> holds. Serialized as a string so API clients can use readable names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardComponentKind>))]
public enum CardComponentKind
{
    /// <summary>Free text, such as a description or cards.</summary>
    Text,
    /// <summary>A checklist, such as acceptance criteria.</summary>
    Checklist,
    /// <summary>A list of links, such as pull requests or documents.</summary>
    Links,
}

/// <summary>
/// One piece of content on a card's own canvas, shown when the card is expanded. A card can have any mix of components;
/// each sits at <see cref="X"/>, <see cref="Y"/> in card pixels (from the top-left of that canvas) and can be moved and
/// resized there. Only the fields that fit <see cref="Kind"/> are used. Stored with the card as one JSON column.
/// </summary>
public class CardComponent
{
    public const int MaxComponents = 30;
    public const int MaxTitleLength = 100;
    public const int MaxTextLength = 4000;
    public const int MaxItems = 50;
    public const int MaxLinks = 20;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public CardComponentKind Kind { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Optional heading, e.g. "Acceptance criteria".</summary>
    public string? Title { get; set; }
    /// <summary>The text of a Text component.</summary>
    public string? Text { get; set; }
    /// <summary>The items of a Checklist component.</summary>
    public List<ChecklistEntry> Items { get; set; } = [];
    /// <summary>The links of a Links component.</summary>
    public List<LinkEntry> Links { get; set; } = [];

    /// <summary>Every piece of text in the component, for search.</summary>
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
