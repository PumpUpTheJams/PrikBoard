using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// A tag defined on a board. Notes pick their tags from this list by name; using a new name on a note adds it to the
/// board. A tag stays on the board when no note uses it.
/// </summary>
/// <param name="Id">Tag identifier, for renameBoardTag and deleteBoardTag.</param>
/// <param name="Name">Lower-case, hyphenated name as shown on post-its, e.g. "back-end".</param>
/// <param name="NoteCount">How many notes on the board have this tag.</param>
public record BoardTagDetail(Guid Id, string Name, int NoteCount)
{
    /// <summary>The board's tags by name, each with the number of <paramref name="notes"/> using it.</summary>
    public static List<BoardTagDetail> List(IEnumerable<BoardTag> tags, IEnumerable<Note> notes)
    {
        var counts = notes.SelectMany(n => n.Tags).CountBy(t => t).ToDictionary();
        return tags.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new BoardTagDetail(t.Id, t.Name, counts.GetValueOrDefault(t.Name)))
            .ToList();
    }
}

/// <summary>Adds a tag to the board without putting it on a note.</summary>
/// <param name="Name">The tag; trimmed, lower-cased and hyphenated ("Back end" becomes "back-end"), at most 30 characters.</param>
public record CreateTagRequest([property: Required, StringLength(100, MinimumLength = 1)] string Name);

/// <summary>Renames a tag on the board and on every note that has it.</summary>
/// <param name="Name">The new name; normalized like createBoardTag. It must not be another tag on the board.</param>
public record RenameTagRequest([property: Required, StringLength(100, MinimumLength = 1)] string Name);
