using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A column with its notes in display order.</summary>
/// <param name="Id">Column identifier.</param>
/// <param name="BoardId">Board the column belongs to.</param>
/// <param name="Name">Column name, e.g. "To do", "Doing" or "Done".</param>
/// <param name="Position">Zero-based position from the left.</param>
/// <param name="Notes">Notes ordered top to bottom.</param>
public record ColumnDetail(Guid Id, Guid BoardId, string Name, int Position, IReadOnlyList<NoteDetail> Notes)
{
    public static ColumnDetail From(Column column) =>
        new(column.Id, column.BoardId, column.Name, column.Position,
            column.Notes.OrderBy(n => n.Position).Select(NoteDetail.From).ToList());
}

/// <summary>Adds a column to a board.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Position">Zero-based position from the left; omit to append at the end.</param>
public record CreateColumnRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Range(0, int.MaxValue)] int? Position = null);

/// <summary>Renames and/or reorders a column. Omitted (null) fields stay unchanged.</summary>
/// <param name="Name">New column name.</param>
/// <param name="Position">New zero-based position from the left.</param>
public record UpdateColumnRequest(
    [property: StringLength(100, MinimumLength = 1)] string? Name = null,
    [property: Range(0, int.MaxValue)] int? Position = null);
