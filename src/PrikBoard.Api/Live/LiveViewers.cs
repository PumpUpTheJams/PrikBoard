using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Live;

/// <summary>
/// The open live connections of this API instance: who they are, which board they look at and with what role. Kept in
/// memory, so it only covers one instance; scaling out needs a SignalR backplane and shared presence.
/// </summary>
public sealed class LiveViewers
{
    /// <summary>One connection. Replaced as a whole on change, so readers never see half an update.</summary>
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
        long LastDragAt = 0);

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

    /// <summary>Who is on a board, each user or agent once.</summary>
    public List<LiveViewer> On(Guid boardId)
    {
        lock (gate)
        {
            return connections.Values.Where(e => e.BoardId == boardId)
                .DistinctBy(e => e.PrincipalId)
                .Select(e => new LiveViewer(e.PrincipalId, e.Kind, e.DisplayName, e.AvatarUrl))
                .ToList();
        }
    }
}
