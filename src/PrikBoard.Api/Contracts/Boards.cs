using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A board without its buckets and notes.</summary>
/// <param name="Id">Board identifier.</param>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board name or description last changed (UTC).</param>
public record BoardSummary(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static BoardSummary From(Board board) =>
        new(board.Id, board.Name, board.Description, board.CreatedAt, board.UpdatedAt);
}

/// <summary>A full board snapshot: every bucket and every note with its position on the canvas.</summary>
/// <param name="Id">Board identifier.</param>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
/// <param name="CreatedAt">When the board was created (UTC).</param>
/// <param name="UpdatedAt">When the board name or description last changed (UTC).</param>
/// <param name="Buckets">Buckets in stacking order, back to front (oldest first).</param>
/// <param name="Notes">Notes grouped by bucket in the order of Buckets, loose notes last, each group top to bottom and left to right.</param>
public record BoardDetail(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BucketDetail> Buckets,
    IReadOnlyList<NoteDetail> Notes)
{
    public static BoardDetail From(Board board) =>
        new(board.Id, board.Name, board.Description, board.CreatedAt, board.UpdatedAt,
            Layout.BackToFront(board.Buckets).Select(BucketDetail.From).ToList(),
            Layout.ReadingOrder(board.Notes, board.Buckets).Select(NoteDetail.From).ToList());
}

/// <summary>Creates a board. It starts with the buckets "To do", "Doing" and "Done" side by side.</summary>
/// <param name="Name">Board name, usually the project name.</param>
/// <param name="Description">Optional free-text description of the project.</param>
public record CreateBoardRequest(
    [property: Required, StringLength(100)] string Name,
    [property: StringLength(1000)] string? Description = null);

/// <summary>Partially updates a board. Omitted (null) fields stay unchanged.</summary>
/// <param name="Name">New board name.</param>
/// <param name="Description">New description; an empty string clears it.</param>
public record UpdateBoardRequest(
    [property: StringLength(100, MinimumLength = 1)] string? Name = null,
    [property: StringLength(1000)] string? Description = null);
