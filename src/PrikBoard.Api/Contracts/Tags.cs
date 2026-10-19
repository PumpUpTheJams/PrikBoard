using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A tag defined on a board. Notes pick their tags from this list by name; using a new name on a note adds it to the
/// board. A tag stays on the board when no note uses it.
/// </summary>
/// <param name="Id">Tag identifier, for updateBoardTag and deleteBoardTag.</param>
/// <param name="Name">Lower-case, hyphenated name as shown on post-its, e.g. "back-end".</param>
/// <param name="Color">How the tag looks on post-its: Default (a shade of the post-it's fill) or a colour of its own.</param>
/// <param name="NoteCount">How many notes on the board have this tag.</param>
public record BoardTagDetail(Guid Id, string Name, TagColor Color, int NoteCount)
{
    public static BoardTagDetail From(BoardTag tag, int noteCount) => new(tag.Id, tag.Name, tag.Color, noteCount);

    /// <summary>The board's tags by name, each with the number of <paramref name="notes"/> using it.</summary>
    public static List<BoardTagDetail> List(IEnumerable<BoardTag> tags, IEnumerable<Note> notes)
    {
        var counts = notes.SelectMany(n => n.Tags).CountBy(t => t).ToDictionary();
        return tags.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => From(t, counts.GetValueOrDefault(t.Name)))
            .ToList();
    }
}

/// <summary>Adds a tag to the board without putting it on a note.</summary>
/// <param name="Name">The tag; trimmed, lower-cased and hyphenated ("Back end" becomes "back-end"), at most 30 characters.</param>
/// <param name="Color">Its colour on post-its; Default when omitted.</param>
public record CreateTagRequest([property: Required, StringLength(100, MinimumLength = 1)] string Name, TagColor? Color = null);

/// <summary>Changes a tag on the board. Omitted fields stay unchanged.</summary>
/// <param name="Name">A new name, normalized like createBoardTag and changed on every note that has the tag. It must not be another tag on the board.</param>
/// <param name="Color">A new colour on post-its; Default goes back to a shade of the post-it's fill.</param>
public record UpdateTagRequest([property: StringLength(100, MinimumLength = 1)] string? Name = null, TagColor? Color = null);
