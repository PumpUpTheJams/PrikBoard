using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A tag defined on a board. Cards pick their tags from this list by name; using a new name on a card adds it to the
/// board. A tag stays on the board when no card uses it.
/// </summary>
/// <param name="Id">Tag identifier, for updateBoardTag and deleteBoardTag.</param>
/// <param name="Name">Lower-case, hyphenated name as shown on cards, e.g. "back-end".</param>
/// <param name="Color">How the tag looks on cards: Default (a shade of the card's fill) or a color of its own.</param>
/// <param name="CardCount">How many cards on the board have this tag.</param>
public record BoardTagDetail(Guid Id, string Name, TagColor Color, int CardCount)
{
    public static BoardTagDetail From(BoardTag tag, int cardCount) => new(tag.Id, tag.Name, tag.Color, cardCount);

    /// <summary>The board's tags by name, each with the number of cards using it (<paramref name="cardTags"/>: each card's tags).</summary>
    public static List<BoardTagDetail> List(IEnumerable<BoardTag> tags, IEnumerable<IEnumerable<string>> cardTags)
    {
        var counts = new Dictionary<string, int>();
        foreach (var tag in cardTags.SelectMany(t => t)) counts[tag] = counts.GetValueOrDefault(tag) + 1;
        return tags.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => From(t, counts.GetValueOrDefault(t.Name)))
            .ToList();
    }
}

/// <summary>Adds a tag to the board without putting it on a card.</summary>
/// <param name="Name">The tag; trimmed, lower-cased and hyphenated ("Back end" becomes "back-end"), at most 30 characters.</param>
/// <param name="Color">Its color on cards; Default when omitted.</param>
public record CreateTagRequest([property: Required, StringLength(100, MinimumLength = 1)] string Name, TagColor? Color = null);

/// <summary>Changes a tag on the board. Omitted fields stay unchanged.</summary>
/// <param name="Name">A new name, normalized like createBoardTag and changed on every card that has the tag. It must not be another tag on the board.</param>
/// <param name="Color">A new color on cards; Default goes back to a shade of the card's fill.</param>
public record UpdateTagRequest([property: StringLength(100, MinimumLength = 1)] string? Name = null, TagColor? Color = null);
