using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Whether a user still exists, for credentials that outlive their user: a session cookie lasts 14 days and a delegated
/// token's user id is cached. Answers are cached for a minute; deleting a user evicts at once on this instance.
/// </summary>
public static class UserState
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private static (string, Guid) Key(Guid userId) => ("user-exists", userId);

    public static async Task<bool> ExistsAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        var cache = services.GetRequiredService<IMemoryCache>();
        if (cache.TryGetValue(Key(userId), out bool exists)) return exists;

        var db = services.GetRequiredService<PrikBoardDbContext>();
        exists = await db.Users.AnyAsync(u => u.Id == userId, ct);
        cache.Set(Key(userId), exists, CacheFor);
        return exists;
    }

    /// <summary>Drops the cached answer, so a deleted user's sessions and tokens stop working at once.</summary>
    public static void Forget(IMemoryCache cache, Guid userId) => cache.Remove(Key(userId));
}
