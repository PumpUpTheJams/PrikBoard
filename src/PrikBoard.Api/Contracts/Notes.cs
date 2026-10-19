using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A post-it note. Every note is 200 by 120 pixels.</summary>
/// <param name="Id">Note identifier.</param>
/// <param name="BoardId">Board the note is stuck on.</param>
/// <param name="BucketId">Bucket containing the note's centre, or null when the note is loose on the board.</param>
/// <param name="Title">Short title shown on the post-it.</param>
/// <param name="Body">Optional longer details, shown when the post-it is expanded.</param>
/// <param name="Color">Post-it fill.</param>
/// <param name="Tags">Names of the board's tags on the note, shown on the post-it, e.g. "backend".</param>
/// <param name="Assignees">People and agents working on the note, in display order, with their current names.</param>
/// <param name="Checklist">Checklist items such as acceptance criteria, in order.</param>
/// <param name="Links">Related links such as pull requests or documents.</param>
/// <param name="AgentWorking">True while an AI agent is actively working on the note. The board shows a lilac halo around it.</param>
/// <param name="X">Left edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="Y">Top edge in canvas pixels, a multiple of the 20 px grid.</param>
/// <param name="CreatedBy">Current display name of whoever stuck the note on the board; null for notes from before this was recorded, or once they no longer exist.</param>
/// <param name="CreatedById">Principal id of whoever created the note, or null (see createdBy).</param>
/// <param name="CreatedAt">When the note was created (UTC).</param>
/// <param name="UpdatedAt">When the note was last edited or moved (UTC). The most recently updated note is drawn on top.</param>
/// <param name="Path">Where the web app shows this note, relative to the site, e.g. "/#/boards/{boardId}/notes/{id}". Use it as a link's url to link this note from another note.</param>
public record NoteDetail(
    Guid Id,
    Guid BoardId,
    Guid? BucketId,
    string Title,
    string? Body,
    NoteColor Color,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Assignee> Assignees,
    IReadOnlyList<ChecklistItem> Checklist,
    IReadOnlyList<NoteLink> Links,
    bool AgentWorking,
    int X,
    int Y,
    string? CreatedBy,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Path)
{
    /// <summary>Maps a note; <paramref name="principals"/> holds its creator and assignees (see NoteEndpoints.PrincipalsOnAsync).</summary>
    public static NoteDetail From(Note note, IReadOnlyDictionary<Guid, Principal> principals) =>
        new(note.Id, note.BoardId, note.BucketId, note.Title, note.Body, note.Color,
            note.Tags,
            note.AssigneeIds.Select(id => Assignee.Of(id, principals)).ToList(),
            note.Checklist.Select(c => new ChecklistItem(c.Text, c.Done)).ToList(),
            note.Links.Select(l => new NoteLink(l.Title, l.Url)).ToList(),
            note.AgentWorking, note.X, note.Y,
            note.CreatedById is { } by && principals.TryGetValue(by, out var creator) ? creator.ShownName : null,
            note.CreatedById, note.CreatedAt, note.UpdatedAt,
            NoteLinks.NotePath(note.BoardId, note.Id));
}

/// <summary>A person or AI agent assigned to a note.</summary>
/// <param name="PrincipalId">Their principal id: what assigneeIds in createNote and updateNote take (see people in getBoard).</param>
/// <param name="Name">Their current display name, e.g. "Jaap V." or "Claude Code"; "Unknown" once they no longer exist. People are shown by their initials.</param>
/// <param name="Kind">Person or Agent. Agents are first-class assignees that can own work.</param>
public record Assignee(Guid PrincipalId, string Name, AssigneeKind Kind)
{
    public static Assignee Of(Guid id, IReadOnlyDictionary<Guid, Principal> principals) =>
        principals.TryGetValue(id, out var principal) ? new(id, principal.ShownName, KindOf(principal)) : new(id, "Unknown", AssigneeKind.Person);

    public static AssigneeKind KindOf(Principal principal) => principal is Agent ? AssigneeKind.Agent : AssigneeKind.Person;
}

/// <summary>One checklist item, e.g. an acceptance criterion.</summary>
/// <param name="Text">What needs to be true or done.</param>
/// <param name="Done">Whether it is ticked off.</param>
public record ChecklistItem(
    [property: Required, StringLength(300, MinimumLength = 1)] string Text,
    bool Done = false);

/// <summary>A link related to a note: a web page, or another note.</summary>
/// <param name="Title">Text shown for the link, e.g. "Retry-After handling (PR #415)".</param>
/// <param name="Url">An absolute http or https address, or another note's path (path in getNote), e.g. "/#/boards/{boardId}/notes/{noteId}". The web app shows a linked note with its current title.</param>
public record NoteLink(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: Required, StringLength(2000)] string Url);

/// <summary>
/// Sticks a new note on a board. Give a bucketId to drop it in a free spot inside that bucket, or X and Y to place it exactly.
/// With neither, it goes into the board's first bucket (or a free spot on the board when there are no buckets).
/// A full bucket grows to make room.
/// </summary>
/// <param name="Title">Short title shown on the post-it.</param>
/// <param name="Body">Optional longer details.</param>
/// <param name="Color">Post-it fill; defaults to Butter.</param>
/// <param name="BucketId">Bucket to put the note in. Do not combine with X and Y.</param>
/// <param name="X">Left edge in canvas pixels; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge in canvas pixels; snapped to the 20 px grid. Give it together with X.</param>
/// <param name="Tags">Tag names; trimmed, lower-cased, hyphenated and de-duplicated, each at most 30 characters. Prefer the board's tags (tags in getBoard); new names are added to the board.</param>
/// <param name="AssigneeIds">Principal ids of people and agents to assign, from people in getBoard (at most 10).</param>
/// <param name="Checklist">Initial checklist items.</param>
/// <param name="Links">Related links.</param>
/// <param name="AgentWorking">Set to true when the calling agent starts working on the note right away.</param>
public record CreateNoteRequest(
    [property: Required, StringLength(200)] string Title,
    [property: StringLength(4000)] string? Body = null,
    NoteColor? Color = null,
    Guid? BucketId = null,
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null,
    [property: MaxLength(10)] List<string>? Tags = null,
    [property: MaxLength(10)] List<Guid>? AssigneeIds = null,
    [property: MaxLength(50)] List<ChecklistItem>? Checklist = null,
    [property: MaxLength(20)] List<NoteLink>? Links = null,
    bool? AgentWorking = null);

/// <summary>
/// Partially updates a note's content. Omitted (null) fields stay unchanged. A list that is given replaces the whole
/// list (send [] to clear it), so to tick off one checklist item, send the full checklist from getNote with that item changed.
/// Use moveNote to move the note.
/// </summary>
/// <param name="Title">New title.</param>
/// <param name="Body">New details; an empty string clears them.</param>
/// <param name="Color">New fill.</param>
/// <param name="Tags">Replacement tag names; new names are added to the board.</param>
/// <param name="AssigneeIds">Replacement assignees by principal id (see people in getBoard). People already on the note may stay after they lose access to the board.</param>
/// <param name="Checklist">Replacement checklist.</param>
/// <param name="Links">Replacement links.</param>
/// <param name="AgentWorking">True when an agent starts working on the note, false when it stops.</param>
public record UpdateNoteRequest(
    [property: StringLength(200, MinimumLength = 1)] string? Title = null,
    [property: StringLength(4000)] string? Body = null,
    NoteColor? Color = null,
    [property: MaxLength(10)] List<string>? Tags = null,
    [property: MaxLength(10)] List<Guid>? AssigneeIds = null,
    [property: MaxLength(50)] List<ChecklistItem>? Checklist = null,
    [property: MaxLength(20)] List<NoteLink>? Links = null,
    bool? AgentWorking = null);

/// <summary>Says the caller is (or is no longer) working on a note.</summary>
/// <param name="Working">True when you start: you are assigned (by your principal id) and the board shows a halo around the post-it. False when you stop: the halo goes, you stay assigned.</param>
public record SetWorkingRequest(bool Working);

/// <summary>
/// Moves a note. Give either a bucketId (the note lands in a free spot inside that bucket, which grows when full)
/// or X and Y (the note goes exactly there and belongs to whichever bucket contains its centre, or none).
/// </summary>
/// <param name="BucketId">Destination bucket on the same board.</param>
/// <param name="X">New left edge in canvas pixels; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge in canvas pixels; snapped to the 20 px grid.</param>
public record MoveNoteRequest(
    Guid? BucketId = null,
    [property: Range(0, Layout.MaxCoordinate)] int? X = null,
    [property: Range(0, Layout.MaxCoordinate)] int? Y = null);
