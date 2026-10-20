using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
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

    public override async Task OnConnectedAsync()
    {
        // AuthSetup.LivePolicy has already checked the scopes, or that a cookie comes from PrikBoard's own pages.
        var user = Context.User!;
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
            await Clients.Group(Group(boardId)).Presence(new BoardPresence(boardId, viewers.On(boardId)));
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Starts following a board (and stops following the previous one). Fails for boards you are not a member of.</summary>
    public async Task<JoinedBoard> Join(Guid boardId)
    {
        var entry = Current();
        var role = await BoardAccess.RoleOnBoardAsync(db, boardId, Context.User!, Context.ConnectionAborted)
                   ?? throw new HubException("Board not found, or you are not a member.");

        if (entry.BoardId is { } previous && previous != boardId) await LeaveBoard(entry, previous);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(boardId));
        entry = viewers.Update(Context.ConnectionId, e => e with { BoardId = boardId, Role = role }) ?? entry;

        var present = viewers.On(boardId);
        await Clients.OthersInGroup(Group(boardId)).Presence(new BoardPresence(boardId, present));
        return new JoinedBoard(boardId, role, entry.Key, present);
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
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(boardId));
        viewers.Update(Context.ConnectionId, e => e with { BoardId = null, Role = BoardRole.Viewer });
        await Clients.Group(Group(boardId)).DragEnded(entry.Key);
        await Clients.Group(Group(boardId)).Presence(new BoardPresence(boardId, viewers.On(boardId)));
    }
}
