using System.Text.Json.Serialization;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>What a drag in progress does, mirroring the web canvas gestures.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DragKind>))]
public enum DragKind
{
    /// <summary>Moving a card.</summary>
    Card,

    /// <summary>Moving a bucket (what is in it moves along).</summary>
    Bucket,

    /// <summary>Resizing a bucket from one of its corners or edges (see <see cref="ResizeHandle"/>).</summary>
    Resize,

    /// <summary>Moving an image.</summary>
    Image,

    /// <summary>Resizing an image from one of its corners or edges (see <see cref="ResizeHandle"/>).</summary>
    ImageResize,

    /// <summary>Moving several selected items together: cards, buckets, images and drawings, listed in <c>Items</c>.</summary>
    Group,
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
/// <param name="BoardId">The board.</param>
/// <param name="Viewers">Its members looking at it. Visitors of a public board that hides people get none: only Guests.</param>
/// <param name="Guests">How many visitors (not members) are looking at the public board. Visitors of a board that hides people get the count of everyone looking instead.</param>
public sealed record BoardPresence(Guid BoardId, List<LiveViewer> Viewers, int Guests = 0);

/// <summary>The answer to Join.</summary>
/// <param name="BoardId">The board joined.</param>
/// <param name="Role">Your role on it (Viewer for a visitor of a public board).</param>
/// <param name="Key">Identifies this connection in drag messages, so a client can tell its own tabs apart.</param>
/// <param name="Viewers">Who is on the board, including you (see BoardPresence).</param>
/// <param name="Guests">See BoardPresence.</param>
/// <param name="Visiting">You joined as a visitor of a public board, not as a member: you hear about changes and presence, not drags or pointers.</param>
public sealed record JoinedBoard(Guid BoardId, BoardRole Role, Guid Key, List<LiveViewer> Viewers, int Guests = 0, bool Visiting = false);

/// <summary>Something on the board was saved; re-read it with getBoard.</summary>
/// <param name="BoardId">The board that changed.</param>
/// <param name="By">Who changed it.</param>
public sealed record BoardChange(Guid BoardId, Guid By);

/// <summary>A drag in progress, sent by the dragging client (Drag), at most every few dozen milliseconds.</summary>
/// <param name="Kind">What the gesture does.</param>
/// <param name="Id">The card, bucket or image being dragged (for Group: the item held).</param>
/// <param name="Dx">Pointer travel since the drag started, in canvas pixels (not yet snapped).</param>
/// <param name="Dy">Pointer travel since the drag started, in canvas pixels (not yet snapped).</param>
/// <param name="Handle">For Resize and ImageResize: the corner or edge being dragged.</param>
/// <param name="Items">
/// For Group: every item moving by this distance, the one held among them (at most <see cref="MaxItems"/>). Items in a
/// bucket that moves aren't listed: the bucket carries them.
/// </param>
/// <param name="Grab">Where the pointer holds the item Id, so others can show the pointer pinned into it.</param>
public sealed record DragUpdate(
    DragKind Kind,
    Guid Id,
    double Dx,
    double Dy,
    ResizeHandle Handle = ResizeHandle.BottomRight,
    List<Guid>? Items = null,
    DragGrab? Grab = null)
{
    /// <summary>The most items one Group drag lists.</summary>
    public const int MaxItems = 1000;
}

/// <summary>Someone else's drag in progress, relayed to the other viewers of the board.</summary>
/// <param name="Key">The dragging connection; DragEnded names the same key.</param>
/// <param name="PrincipalId">Who is dragging.</param>
/// <param name="DisplayName">Their display name, to label the item.</param>
/// <param name="Kind">What the gesture does.</param>
/// <param name="Id">The card, bucket or image being dragged.</param>
/// <param name="Dx">Pointer travel since the drag started, in canvas pixels.</param>
/// <param name="Dy">Pointer travel since the drag started, in canvas pixels.</param>
/// <param name="Handle">For Resize and ImageResize: the corner or edge being dragged.</param>
/// <param name="Items">For Group: every item moving by this distance (see <see cref="DragUpdate"/>).</param>
/// <param name="Grab">Where the pointer holds the item Id, when the dragging client says.</param>
public sealed record RemoteDrag(
    Guid Key,
    Guid PrincipalId,
    string DisplayName,
    DragKind Kind,
    Guid Id,
    double Dx,
    double Dy,
    ResizeHandle Handle = ResizeHandle.BottomRight,
    List<Guid>? Items = null,
    DragGrab? Grab = null);

/// <summary>
/// Where on the dragged item the pointer took hold of it, as fractions of the item's width and height when the drag
/// started (0,0 its top-left corner, 1,1 its bottom-right). It stays at the same fraction of the item as it moves or resizes.
/// </summary>
/// <param name="X">From 0 (the left edge) to 1 (the right edge).</param>
/// <param name="Y">From 0 (the top edge) to 1 (the bottom edge).</param>
public sealed record DragGrab(double X, double Y)
{
    [JsonIgnore]
    public bool IsValid => X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

/// <summary>Where this connection's pointer is on the joined board (MoveCursor), at most every few dozen milliseconds.</summary>
/// <param name="X">Canvas pixels, like the x of a card.</param>
/// <param name="Y">Canvas pixels, like the y of a card.</param>
public sealed record CursorUpdate(double X, double Y);

/// <summary>Another member's pointer on the joined board. Their name and picture are in Presence.</summary>
/// <param name="Key">The connection pointing; CursorHidden names the same key.</param>
/// <param name="PrincipalId">Who is pointing.</param>
/// <param name="X">Canvas pixels.</param>
/// <param name="Y">Canvas pixels.</param>
public sealed record RemoteCursor(Guid Key, Guid PrincipalId, double X, double Y);
