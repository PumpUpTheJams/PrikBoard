using System.Text.Json.Serialization;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>What a drag in progress does, mirroring the web canvas gestures.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DragKind>))]
public enum DragKind
{
    /// <summary>Moving a note.</summary>
    Note,

    /// <summary>Moving a bucket (its notes move along).</summary>
    Bucket,

    /// <summary>Resizing a bucket from one of its corners or edges (see <see cref="ResizeHandle"/>).</summary>
    Resize,

    /// <summary>Moving an image.</summary>
    Image,

    /// <summary>Resizing an image from one of its corners or edges (see <see cref="ResizeHandle"/>).</summary>
    ImageResize,
}

/// <summary>
/// The corner or edge a resize drags. The opposite corner or edge stays where it is; an edge changes the size in its own
/// direction only.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResizeHandle>))]
public enum ResizeHandle
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>Someone looking at a board right now. A person with two tabs open is listed once.</summary>
/// <param name="PrincipalId">The user or agent.</param>
/// <param name="Kind">User or Agent.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="AvatarUrl">People only: their chosen picture (see getAvatar); null without one.</param>
public sealed record LiveViewer(Guid PrincipalId, PrincipalKind Kind, string DisplayName, string? AvatarUrl);

/// <summary>Who is on a board right now; sent whenever someone joins or leaves it.</summary>
public sealed record BoardPresence(Guid BoardId, List<LiveViewer> Viewers);

/// <summary>The answer to Join.</summary>
/// <param name="BoardId">The board joined.</param>
/// <param name="Role">Your role on it.</param>
/// <param name="Key">Identifies this connection in drag messages, so a client can tell its own tabs apart.</param>
/// <param name="Viewers">Who is on the board, including you.</param>
public sealed record JoinedBoard(Guid BoardId, BoardRole Role, Guid Key, List<LiveViewer> Viewers);

/// <summary>Something on the board was saved; re-read it with getBoard.</summary>
/// <param name="BoardId">The board that changed.</param>
/// <param name="By">Who changed it.</param>
public sealed record BoardChange(Guid BoardId, Guid By);

/// <summary>A drag in progress, sent by the dragging client (Drag), at most every few dozen milliseconds.</summary>
/// <param name="Kind">What the gesture does.</param>
/// <param name="Id">The note, bucket or image being dragged.</param>
/// <param name="Dx">Pointer travel since the drag started, in canvas pixels (not yet snapped).</param>
/// <param name="Dy">Pointer travel since the drag started, in canvas pixels (not yet snapped).</param>
/// <param name="Handle">For Resize and ImageResize: the corner or edge being dragged.</param>
public sealed record DragUpdate(DragKind Kind, Guid Id, double Dx, double Dy, ResizeHandle Handle = ResizeHandle.BottomRight);

/// <summary>Someone else's drag in progress, relayed to the other viewers of the board.</summary>
/// <param name="Key">The dragging connection; DragEnded names the same key.</param>
/// <param name="PrincipalId">Who is dragging.</param>
/// <param name="DisplayName">Their display name, to label the item.</param>
/// <param name="Kind">What the gesture does.</param>
/// <param name="Id">The note, bucket or image being dragged.</param>
/// <param name="Dx">Pointer travel since the drag started, in canvas pixels.</param>
/// <param name="Dy">Pointer travel since the drag started, in canvas pixels.</param>
/// <param name="Handle">For Resize and ImageResize: the corner or edge being dragged.</param>
public sealed record RemoteDrag(
    Guid Key, Guid PrincipalId, string DisplayName, DragKind Kind, Guid Id, double Dx, double Dy, ResizeHandle Handle = ResizeHandle.BottomRight);
