using Microsoft.EntityFrameworkCore;

namespace PrikBoard.Api.Data;

/// <summary>
/// Deleted notes, buckets, images and tags stay in their tables (with <c>DeletedAt</c> set, hidden by the
/// <see cref="PrikBoardDbContext.SoftDelete"/> filter) so they can be restored, which is what undo in the web app does.
/// After <see cref="Retention"/> they are deleted for good.
/// </summary>
public static class Tombstones
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>How long deleted things can be restored, for endpoint descriptions.</summary>
    public const string RetentionText = "7 days";

    /// <summary>Deletes for good everything deleted before <paramref name="cutoff"/>. Returns how many rows went.</summary>
    public static async Task<int> PurgeAsync(PrikBoardDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        string[] deleted = [PrikBoardDbContext.SoftDelete];
        // Images first; a note's images and their bytes would also go with it through the cascades.
        var count = await db.Images.IgnoreQueryFilters(deleted).Where(i => i.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.Notes.IgnoreQueryFilters(deleted).Where(n => n.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        // Notes and images still pointing at a purged bucket lose the reference (SetNull); live notes and images never do, see Layout.Release.
        count += await db.Buckets.IgnoreQueryFilters(deleted).Where(b => b.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        count += await db.BoardTags.IgnoreQueryFilters(deleted).Where(t => t.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        return count;
    }
}

/// <summary>Runs <see cref="Tombstones.PurgeAsync"/> every hour.</summary>
public sealed class TombstonePurger(IServiceScopeFactory scopes, ILogger<TombstonePurger> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of the migrations that run at startup.
            await Task.Delay(FirstRun, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await PurgeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        // An exception escaping a BackgroundService stops the host, so a failed run is only logged; the next one retries.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var purged = await Tombstones.PurgeAsync(db, DateTimeOffset.UtcNow - Tombstones.Retention, ct);
            if (purged > 0) logger.LogInformation("Purged {Count} deleted items older than {Retention}", purged, Tombstones.Retention);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Purging deleted items failed");
        }
    }
}
