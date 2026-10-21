using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Live;

/// <summary>
/// Tells live clients about saved changes. Called after the database work succeeded; a failure here is logged and never
/// fails the request, because clients also re-read the board now and then.
/// </summary>
public sealed class BoardEvents(IHubContext<BoardHub, IBoardClient> hub, LiveViewers viewers, ILogger<BoardEvents> logger)
{
    /// <summary>
    /// Sent by the web app with its own live connection id, so the tab that made a change (and reloads anyway) is not told
    /// about it again. Only the caller's own connections are skipped.
    /// </summary>
    public const string ConnectionHeader = "X-PrikBoard-Live";

    /// <summary>Something on the board was saved.</summary>
    public Task ChangedAsync(Guid boardId, HttpContext http) => Safely(() =>
    {
        var by = http.User.PrincipalId();
        var skip = http.Request.Headers[ConnectionHeader]
            .Where(id => id is not null && viewers.Get(id)?.PrincipalId == by)
            .Select(id => id!)
            .ToList();
        return hub.Clients.GroupExcept(BoardHub.Group(boardId), skip).BoardChanged(new BoardChange(boardId, by));
    });

    /// <summary>Something on the board was saved by <paramref name="by"/> outside their own request (an image upload URL), so no connection is skipped.</summary>
    public Task ChangedAsync(Guid boardId, Guid by) => Safely(() =>
        hub.Clients.Group(BoardHub.Group(boardId)).BoardChanged(new BoardChange(boardId, by)));

    /// <summary>These users' or agents' board lists changed.</summary>
    public Task BoardsChangedAsync(IEnumerable<Guid> principalIds) => Safely(() =>
        hub.Clients.Users(principalIds.Select(id => id.ToString()).ToList()).BoardsChanged());

    /// <summary>The board lists of every member of a board changed (renamed, or about to be deleted).</summary>
    public async Task BoardsChangedForMembersAsync(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        await BoardsChangedAsync(await MembersOf(db, boardId, ct));

    public static Task<List<Guid>> MembersOf(PrikBoardDbContext db, Guid boardId, CancellationToken ct) =>
        db.BoardMembers.Where(m => m.BoardId == boardId).Select(m => m.PrincipalId).ToListAsync(ct);

    /// <summary>
    /// A member's role changed (<paramref name="role"/>), or they were removed (null). Live connections pick up the new
    /// role at once; removed ones stop hearing about the board.
    /// </summary>
    public Task AccessChangedAsync(Guid boardId, Guid principalId, BoardRole? role) => Safely(async () =>
    {
        foreach (var entry in viewers.OfPrincipal(principalId, boardId))
        {
            if (role is { } kept)
            {
                viewers.Update(entry.ConnectionId, e => e with { Role = kept });
                continue;
            }

            await hub.Groups.RemoveFromGroupAsync(entry.ConnectionId, BoardHub.Group(boardId));
            viewers.Update(entry.ConnectionId, e => e with { BoardId = null, Role = BoardRole.Viewer });
            await hub.Clients.Group(BoardHub.Group(boardId)).DragEnded(entry.Key);
        }
        if (role is null) await PresenceAsync(boardId);
        await hub.Clients.User(principalId.ToString()).AccessChanged(boardId);
    });

    /// <summary>Someone picked a new display name or avatar; the boards they are on show it.</summary>
    public Task ProfileChangedAsync(Principal principal) => Safely(async () =>
    {
        var boards = new HashSet<Guid>();
        foreach (var entry in viewers.OfPrincipal(principal.Id))
        {
            viewers.Update(entry.ConnectionId, e => e with { DisplayName = principal.ShownName, AvatarUrl = Avatars.UrlOf(principal) });
            if (entry.BoardId is { } boardId) boards.Add(boardId);
        }
        foreach (var boardId in boards) await PresenceAsync(boardId);
    });

    /// <summary>
    /// The caller picked a new display name or avatar, which cards show (creator, assignee): every board they can reach reloads.
    /// </summary>
    public async Task ProfileShownOnBoardsAsync(PrikBoardDbContext db, HttpContext http, CancellationToken ct)
    {
        var caller = BoardCaller.Of(http.User);
        var memberships = await db.BoardMembers.Where(m => m.PrincipalId == caller.MemberId)
            .Select(m => new { m.BoardId, m.Role }).ToListAsync(ct);
        foreach (var membership in memberships.Where(m => caller.Apply(m.BoardId, m.Role) is not null))
        {
            await ChangedAsync(membership.BoardId, http);
        }
    }

    private Task PresenceAsync(Guid boardId) =>
        hub.Clients.Group(BoardHub.Group(boardId)).Presence(new BoardPresence(boardId, viewers.On(boardId)));

    private async Task Safely(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not send a live update");
        }
    }
}

/// <summary>SignalR's user id is the principal id, so events can go to a user or agent on all of their connections.</summary>
public sealed class PrincipalUserIds : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User.Identity?.IsAuthenticated == true ? connection.User.PrincipalId().ToString() : null;
}
