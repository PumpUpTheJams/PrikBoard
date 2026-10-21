using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PrikBoard.Discord;

/// <summary>
/// /prikboard connect runs in progress: per server, the PKCE verifier and the channel it was run in, for 10 minutes and
/// in memory only. A code approved for one server can't be finished in another, which has no matching verifier; a
/// restart just means running /prikboard connect again.
/// </summary>
public sealed class PendingConnects(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public sealed record Pending(string Verifier, ulong ChannelId, string ChannelName, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<ulong, Pending> _pending = new();

    /// <summary>Starts (or restarts) connecting the server from this channel; returns the PKCE challenge for the link.</summary>
    public string Start(ulong guildId, ulong channelId, string channelName)
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _pending[guildId] = new Pending(verifier, channelId, channelName, time.GetUtcNow().Add(Lifetime));
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>Takes the server's connect in progress (once), or null when there is none or it expired.</summary>
    public Pending? Take(ulong guildId) =>
        _pending.TryRemove(guildId, out var pending) && pending.ExpiresAt > time.GetUtcNow() ? pending : null;
}
