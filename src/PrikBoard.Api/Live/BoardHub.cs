using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Live;

/// <summary>What the server sends to live clients.</summary>
public interface IBoardClient
{
    /// <summary>Something on the joined board was saved (by anyone but this connection); re-read it with getBoard.</summary>
    Task BoardChanged(BoardChange change);

    /// <summary>Someone joined or left the joined board.</summary>
    Task Presence(BoardPresence presence);

    /// <summary>Another viewer is dragging something on the joined board.</summary>
    Task Dragging(RemoteDrag drag);

    /// <summary>That viewer let go (or left). Keep showing the dragged item until the next getBoard shows where it landed.</summary>
    Task DragEnded(Guid key);

    /// <summary>Another member's pointer moved on the joined board. Members only, and only while the board shows cursors.</summary>
    Task CursorMoved(RemoteCursor cursor);

    /// <summary>That member's pointer left the board (or they did).</summary>
    Task CursorHidden(Guid key);

    /// <summary>Another member's selection on the joined board changed (no items: they selected nothing, or left).</summary>
    Task Selected(RemoteSelection selection);

    /// <summary>Your list of boards changed (a board was renamed or deleted, or you were added or removed); re-read listBoards.</summary>
    Task BoardsChanged();

    /// <summary>Your role on a board changed or you lost access; re-join it (or leave it when Join answers null).</summary>
    Task AccessChanged(Guid boardId);
}

/// <summary>
/// Live updates at /api/live (SignalR). A connection joins one board at a time and then hears about saved changes, who
/// else is there, drags in progress and (for members) where the others point. Changes themselves still go through the REST endpoints.
/// </summary>
public sealed class BoardHub(LiveViewers viewers, PrikBoardDbContext db) : Hub<IBoardClient>
{
    /// <summary>Drag updates closer together than this are dropped; clients send about every 50 ms.</summary>
    private static readonly long DragInterval = Stopwatch.Frequency / 40;

    /// <summary>Anything further is not a drag on a board.</summary>
    private const double MaxTravel = 100_000;

    public static string Group(Guid boardId) => $"board:{boardId}";

    /// <summary>Visitors of a public board: they hear about saved changes and presence, never drags or pointers.</summary>
    public static string VisitorGroup(Guid boardId) => $"board:{boardId}:visitors";

    /// <summary>Tells a board's members and its visitors who is there, each as they may see it, but not <paramref name="except"/>.</summary>
    public static async Task SendPresenceAsync(IHubClients<IBoardClient> clients, LiveViewers viewers, Guid boardId, string? except = null)
    {
        string[] skip = except is null ? [] : [except];
        await clients.GroupExcept(Group(boardId), skip).Presence(viewers.Presence(boardId, forVisitors: false));
        await clients.GroupExcept(VisitorGroup(boardId), skip).Presence(viewers.Presence(boardId, forVisitors: true));
    }

    public override async Task OnConnectedAsync()
    {
        // AuthSetup.LivePolicy has already checked the scopes, or that the connection comes from PrikBoard's own pages.
        var user = Context.User!;
        if (user.Identity?.IsAuthenticated != true)
        {
            // Not signed in: only public boards can be joined, as a guest.
            viewers.Add(new LiveViewers.Entry(Context.ConnectionId, Guid.NewGuid(), Guid.NewGuid(), PrincipalKind.User, "Guest", null, false));
            await base.OnConnectedAsync();
            return;
        }
        var principal = await db.Principals.FindAsync([user.PrincipalId()], Context.ConnectionAborted)
                        ?? throw new HubException("Unknown user.");
        viewers.Add(new LiveViewers.Entry(
            Context.ConnectionId,
            Guid.NewGuid(),
            principal.Id,
            principal is Agent ? PrincipalKind.Agent : PrincipalKind.User,
            principal.ShownName,
            Avatars.UrlOf(principal),
            ApiScopes.Has(user, ApiScopes.Write)));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (viewers.Remove(Context.ConnectionId) is { BoardId: { } boardId } entry)
        {
            await GoneAsync(Clients, boardId, entry);
            await SendPresenceAsync(Clients, viewers, boardId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Starts following a board (and stops following the previous one). Members join as such; anyone else (but tokens) can
    /// join a public board as a visitor. Answers null for other boards (and keeps following the previous one): a tab can
    /// still have a board open that is gone, so that is no error worth logging.
    /// </summary>
    public async Task<JoinedBoard?> Join(Guid boardId)
    {
        var entry = Current();
        var user = Context.User!;
        BoardRole? role;
        var showsPeople = false;
        var cursors = false;
        try
        {
            role = await BoardAccess.RoleOnBoardAsync(db, boardId, user, Context.ConnectionAborted);
            if (role is not null)
            {
                cursors = await db.Boards.Where(b => b.Id == boardId).Select(b => b.ShowCursors).FirstOrDefaultAsync(Context.ConnectionAborted);
            }
            else
            {
                var board = user.IsToken()
                    ? null
                    : await db.Boards.Where(b => b.Id == boardId && b.IsPublic).Select(b => new { b.PublicShowsPeople }).FirstOrDefaultAsync(Context.ConnectionAborted);
                if (board is null) return null;
                showsPeople = board.PublicShowsPeople;
            }
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // The tab closed mid-join; nobody is waiting for the answer.
            return null;
        }
        var visitor = role is null;

        // Also when only the way in changed (a visitor who became a member, or the other way round).
        if (entry.BoardId is { } previous && (previous != boardId || entry.Visitor != visitor)) await LeaveBoard(entry, previous);
        await Groups.AddToGroupAsync(Context.ConnectionId, visitor ? VisitorGroup(boardId) : Group(boardId));
        entry = viewers.Update(Context.ConnectionId, e => e with
        {
            BoardId = boardId, Role = role ?? BoardRole.Viewer, Visitor = visitor, ShowsPeople = showsPeople, Cursors = cursors,
        }) ?? entry;

        await SendPresenceAsync(Clients, viewers, boardId, except: Context.ConnectionId);
        // Pointers held still since they last moved, and selections made before, would not show until they change.
        if (entry.Cursors)
        {
            foreach (var other in viewers.MembersOn(boardId).Where(e => e.ConnectionId != Context.ConnectionId))
            {
                if (other.Cursor is { } at) await Clients.Caller.CursorMoved(new RemoteCursor(other.Key, other.PrincipalId, at.X, at.Y));
                if (other.Selection is { } items) await Clients.Caller.Selected(new RemoteSelection(other.Key, other.PrincipalId, items));
            }
        }
        var present = viewers.Presence(boardId, forVisitors: visitor);
        return new JoinedBoard(boardId, entry.Role, entry.Key, present.Viewers, present.Guests, visitor);
    }

    /// <summary>Stops following the current board.</summary>
    public async Task Leave()
    {
        var entry = Current();
        if (entry.BoardId is { } boardId) await LeaveBoard(entry, boardId);
    }

    /// <summary>Shows the other viewers a drag in progress. Needs the Editor role (and boards:write for tokens).</summary>
    public async Task Drag(DragUpdate update)
    {
        var entry = Current();
        if (entry.BoardId is not { } boardId) return;
        if (entry.Role < BoardRole.Editor || !entry.CanWrite) throw new HubException("Dragging needs the Editor role on the board.");
        if (!double.IsFinite(update.Dx) || !double.IsFinite(update.Dy) || Math.Abs(update.Dx) > MaxTravel || Math.Abs(update.Dy) > MaxTravel)
        {
            throw new HubException("That is not a drag on the board.");
        }

        if (update.Kind == DragKind.Group ? update.Items is not { Count: > 0 and <= DragUpdate.MaxItems } : update.Items is not null)
        {
            throw new HubException($"A Group drag lists between 1 and {DragUpdate.MaxItems} items; other drags list none.");
        }

        if (!viewers.TakeDragTurn(Context.ConnectionId, Stopwatch.GetTimestamp(), DragInterval)) return;

        await Clients.OthersInGroup(Group(boardId)).Dragging(new RemoteDrag(
            entry.Key, entry.PrincipalId, entry.DisplayName, update.Kind, update.Id, update.Dx, update.Dy, update.Handle, update.Items));
    }

    /// <summary>The drag is over (dropped or canceled).</summary>
    public async Task DragEnd()
    {
        var entry = Current();
        if (entry.BoardId is { } boardId) await Clients.OthersInGroup(Group(boardId)).DragEnded(entry.Key);
    }

    /// <summary>
    /// Shows the other members where this connection points on the board. Any member may, while the board shows cursors;
    /// visitors never do.
    /// </summary>
    public async Task MoveCursor(CursorUpdate update)
    {
        var entry = Current();
        if (entry.BoardId is not { } boardId) return;
        if (entry.Visitor || !entry.Cursors) throw new HubException("This board doesn't show cursors.");
        if (!double.IsFinite(update.X) || !double.IsFinite(update.Y)
            || Math.Abs(update.X) > Layout.MaxCoordinate || Math.Abs(update.Y) > Layout.MaxCoordinate)
        {
            throw new HubException("That is not a point on the board.");
        }

        if (!viewers.TakeCursorTurn(Context.ConnectionId, update, Stopwatch.GetTimestamp(), DragInterval)) return;

        await Clients.OthersInGroup(Group(boardId)).CursorMoved(new RemoteCursor(entry.Key, entry.PrincipalId, update.X, update.Y));
    }

    /// <summary>The pointer left the board (out of the window, or the tab went to the background).</summary>
    public async Task HideCursor()
    {
        var entry = viewers.Update(Context.ConnectionId, e => e with { Cursor = null }) ?? Current();
        if (entry.BoardId is { } boardId && !entry.Visitor) await Clients.OthersInGroup(Group(boardId)).CursorHidden(entry.Key);
    }

    /// <summary>
    /// Shows the other members what this connection has selected, as its pins in those items. Any member may, while the
    /// board shows cursors; visitors never do.
    /// </summary>
    public async Task Select(SelectionUpdate update)
    {
        var entry = Current();
        if (entry.BoardId is not { } boardId) return;
        if (entry.Visitor || !entry.Cursors) throw new HubException("This board doesn't show cursors.");
        if (update.Items is not { Count: <= DragUpdate.MaxItems }) throw new HubException($"A selection lists at most {DragUpdate.MaxItems} items.");

        var items = update.Items.Distinct().ToList();
        viewers.Update(Context.ConnectionId, e => e with { Selection = items.Count > 0 ? items : null });
        await Clients.OthersInGroup(Group(boardId)).Selected(new RemoteSelection(entry.Key, entry.PrincipalId, items));
    }

    /// <summary>A connection left the board's members: its drag ends, and its pointer and pins go.</summary>
    public static async Task GoneAsync(IHubClients<IBoardClient> clients, Guid boardId, LiveViewers.Entry entry)
    {
        await clients.Group(Group(boardId)).DragEnded(entry.Key);
        await clients.Group(Group(boardId)).CursorHidden(entry.Key);
        if (entry.Selection is not null) await clients.Group(Group(boardId)).Selected(new RemoteSelection(entry.Key, entry.PrincipalId, []));
    }

    private LiveViewers.Entry Current() =>
        viewers.Get(Context.ConnectionId) ?? throw new HubException("This connection is closing.");

    private async Task LeaveBoard(LiveViewers.Entry entry, Guid boardId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, entry.Visitor ? VisitorGroup(boardId) : Group(boardId));
        viewers.Update(Context.ConnectionId, e => e with { BoardId = null, Role = BoardRole.Viewer, Visitor = false, ShowsPeople = false, Cursors = false, Cursor = null, Selection = null });
        await GoneAsync(Clients, boardId, entry);
        await SendPresenceAsync(Clients, viewers, boardId);
    }
}
