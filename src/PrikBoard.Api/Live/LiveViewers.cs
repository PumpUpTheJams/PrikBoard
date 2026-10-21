using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Live;

/// <summary>
/// The open live connections of this API instance: who they are, which board they look at and with what role. Kept in
/// memory, so it only covers one instance; scaling out needs a SignalR backplane and shared presence.
/// </summary>
public sealed class LiveViewers
{
    /// <summary>
    /// One connection. Replaced as a whole on change, so readers never see half an update. Someone not signed in has a
    /// principal id of their own connection's. <paramref name="Visitor"/>: on a public board without being a member,
    /// which shows people to its visitors or not (<paramref name="ShowsPeople"/>).
    /// </summary>
    public sealed record Entry(
        string ConnectionId,
        Guid Key,
        Guid PrincipalId,
        PrincipalKind Kind,
        string DisplayName,
        string? AvatarUrl,
        bool CanWrite,
        Guid? BoardId = null,
        BoardRole Role = BoardRole.Viewer,
        long LastDragAt = 0,
        bool Visitor = false,
        bool ShowsPeople = false);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> connections = [];

    public void Add(Entry entry)
    {
        lock (gate) connections[entry.ConnectionId] = entry;
    }

    public Entry? Get(string connectionId)
    {
        lock (gate) return connections.GetValueOrDefault(connectionId);
    }

    /// <summary>Applies a change to a connection that is still open; answers the new entry, or null when it is gone.</summary>
    public Entry? Update(string connectionId, Func<Entry, Entry> change)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(connectionId, out var entry)) return null;
            return connections[connectionId] = change(entry);
        }
    }

    public Entry? Remove(string connectionId)
    {
        lock (gate) return connections.Remove(connectionId, out var entry) ? entry : null;
    }

    /// <summary>The connections of one user or agent, optionally only those on a board.</summary>
    public List<Entry> OfPrincipal(Guid principalId, Guid? boardId = null)
    {
        lock (gate) return connections.Values.Where(e => e.PrincipalId == principalId && (boardId is null || e.BoardId == boardId)).ToList();
    }

    /// <summary>The visitors' connections on a public board.</summary>
    public List<Entry> VisitorsOn(Guid boardId)
    {
        lock (gate) return connections.Values.Where(e => e.BoardId == boardId && e.Visitor).ToList();
    }

    /// <summary>
    /// Who is on a board, for its members (members listed, each user or agent once, and how many visitors) or for its
    /// visitors: the same when the board shows them people (with pictures served for the board), else only how many look.
    /// </summary>
    public BoardPresence Presence(Guid boardId, bool forVisitors)
    {
        lock (gate)
        {
            var here = connections.Values.Where(e => e.BoardId == boardId).ToList();
            var members = here.Where(e => !e.Visitor)
                .DistinctBy(e => e.PrincipalId)
                .Select(e => new LiveViewer(e.PrincipalId, e.Kind, e.DisplayName, e.AvatarUrl))
                .ToList();
            var guests = here.Where(e => e.Visitor).DistinctBy(e => e.PrincipalId).Count();
            if (!forVisitors) return new BoardPresence(boardId, members, guests);
            return here.Any(e => e.Visitor && e.ShowsPeople)
                ? new BoardPresence(boardId, [.. members.Select(v => v with { AvatarUrl = Avatars.OnBoard(v.AvatarUrl, boardId) })], guests)
                : new BoardPresence(boardId, [], members.Count + guests);
        }
    }
}
