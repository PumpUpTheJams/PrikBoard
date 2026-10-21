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

    /// <summary>Your list of boards changed (a board was renamed or deleted, or you were added or removed); re-read listBoards.</summary>
    Task BoardsChanged();

    /// <summary>Your role on a board changed or you lost access; re-join it (or leave it when Join fails).</summary>
    Task AccessChanged(Guid boardId);
}

/// <summary>
/// Live updates at /api/live (SignalR). A connection joins one board at a time and then hears about saved changes, who
/// else is there and drags in progress. Changes themselves still go through the REST endpoints.
/// </summary>
public sealed class BoardHub(LiveViewers viewers, PrikBoardDbContext db) : Hub<IBoardClient>
{
    /// <summary>Drag updates closer together than this are dropped; clients send about every 50 ms.</summary>
    private static readonly long DragInterval = Stopwatch.Frequency / 40;

    /// <summary>Anything further is not a drag on a board.</summary>
    private const double MaxTravel = 100_000;

    public static string Group(Guid boardId) => $"board:{boardId}";

    /// <summary>Visitors of a public board: they hear about saved changes and presence, never drags.</summary>
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
            ApiScopes.Of(user).Contains(ApiScopes.Write)));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (viewers.Remove(Context.ConnectionId) is { BoardId: { } boardId } entry)
        {
            await Clients.Group(Group(boardId)).DragEnded(entry.Key);
            await SendPresenceAsync(Clients, viewers, boardId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Starts following a board (and stops following the previous one). Members join as such; anyone else (but tokens) can
    /// join a public board as a visitor. Fails for other boards.
    /// </summary>
    public async Task<JoinedBoard> Join(Guid boardId)
    {
        var entry = Current();
        var user = Context.User!;
        var role = await BoardAccess.RoleOnBoardAsync(db, boardId, user, Context.ConnectionAborted);
        var visitor = role is null;
        var showsPeople = false;
        if (visitor)
        {
            var board = user.IsToken()
                ? null
                : await db.Boards.Where(b => b.Id == boardId && b.IsPublic).Select(b => new { b.PublicShowsPeople }).FirstOrDefaultAsync(Context.ConnectionAborted);
            if (board is null) throw new HubException("Board not found, or you are not a member.");
            showsPeople = board.PublicShowsPeople;
        }

        // Also when only the way in changed (a visitor who became a member, or the other way round).
        if (entry.BoardId is { } previous && (previous != boardId || entry.Visitor != visitor)) await LeaveBoard(entry, previous);
        await Groups.AddToGroupAsync(Context.ConnectionId, visitor ? VisitorGroup(boardId) : Group(boardId));
        entry = viewers.Update(Context.ConnectionId, e => e with
        {
            BoardId = boardId, Role = role ?? BoardRole.Viewer, Visitor = visitor, ShowsPeople = showsPeople,
        }) ?? entry;

        await SendPresenceAsync(Clients, viewers, boardId, except: Context.ConnectionId);
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

        var now = Stopwatch.GetTimestamp();
        if (now - entry.LastDragAt < DragInterval) return;
        viewers.Update(Context.ConnectionId, e => e with { LastDragAt = now });

        await Clients.OthersInGroup(Group(boardId)).Dragging(
            new RemoteDrag(entry.Key, entry.PrincipalId, entry.DisplayName, update.Kind, update.Id, update.Dx, update.Dy, update.Handle, update.Items));
    }

    /// <summary>The drag is over (dropped or cancelled).</summary>
    public async Task DragEnd()
    {
        var entry = Current();
        if (entry.BoardId is { } boardId) await Clients.OthersInGroup(Group(boardId)).DragEnded(entry.Key);
    }

    private LiveViewers.Entry Current() =>
        viewers.Get(Context.ConnectionId) ?? throw new HubException("This connection is closing.");

    private async Task LeaveBoard(LiveViewers.Entry entry, Guid boardId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, entry.Visitor ? VisitorGroup(boardId) : Group(boardId));
        viewers.Update(Context.ConnectionId, e => e with { BoardId = null, Role = BoardRole.Viewer, Visitor = false, ShowsPeople = false });
        await Clients.Group(Group(boardId)).DragEnded(entry.Key);
        await SendPresenceAsync(Clients, viewers, boardId);
    }
}
