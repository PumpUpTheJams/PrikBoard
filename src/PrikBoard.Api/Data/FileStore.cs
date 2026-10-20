using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Keeps the contents of <see cref="StoredFile"/>s (images, avatars) on disk under <c>Storage:Path</c>, which is a volume
/// of its own in production. A file is named after its id (<c>ab/0199…ab</c>, the folder being the id's last two hex
/// digits), so names on disk say nothing about what is in them; the original name is in the row. Small files are kept in
/// memory as well, up to <c>Storage:MemoryCacheBytes</c> in all.
/// </summary>
/// <remarks>
/// The file is written before its row is committed, and a row is only inserted together with the row that refers to it.
/// A failed save or a crash therefore leaves at most a file without a row, never a row without a file. <see cref="SweepAsync"/>
/// deletes both rows nothing refers to (deleted boards and users, purged images, replaced avatars) and such stray files.
/// </remarks>
public sealed class FileStore : IDisposable
{
    public const string PathKey = "Storage:Path";

    /// <summary>Rows nothing refers to stay this long, so a sweep never races a request that is just adding one.</summary>
    private static readonly TimeSpan UnreferencedGrace = TimeSpan.FromMinutes(10);

    /// <summary>A file on disk without a row stays this long, so a sweep never races an upload that is still saving.</summary>
    private static readonly TimeSpan StrayGrace = TimeSpan.FromDays(1);

    /// <summary>More stray files than this in one sweep looks like the wrong database rather than leftovers: none are deleted.</summary>
    private const int MaxStraysPerSweep = 100;

    private readonly MemoryCache _cache;
    private readonly long _maxCachedFileBytes;
    private readonly ILogger _logger;

    /// <summary>The full path of the folder holding the files.</summary>
    public string Root { get; }

    /// <summary>
    /// Creates the folder when needed and checks that files can be written there, so a missing or read-only volume stops
    /// startup instead of the first upload.
    /// </summary>
    public FileStore(string root, ILogger logger, long memoryCacheBytes = 64 * 1024 * 1024, long maxCachedFileBytes = 1024 * 1024)
    {
        Root = Path.GetFullPath(root);
        _logger = logger;
        _maxCachedFileBytes = maxCachedFileBytes;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = memoryCacheBytes });
        try
        {
            Directory.CreateDirectory(Root);
            var probe = Path.Combine(Root, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{PathKey} ({Root}) is not a folder the API can write to: {error.Message}", error);
        }
    }

    /// <summary>
    /// Reads <c>Storage:Path</c> and the memory cache sizes. The path is required outside Development (which uses a "files"
    /// folder next to the app), so the API never puts files where a restart would lose them.
    /// </summary>
    public static FileStore FromConfiguration(IConfiguration configuration, IHostEnvironment environment, ILogger<FileStore> logger)
    {
        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{PathKey} is not set. Point it at a persistent folder (a volume of its own) for images and avatars.");
            }
            path = Path.Combine(environment.ContentRootPath, "files");
        }
        return new FileStore(path, logger,
            configuration.GetValue("Storage:MemoryCacheBytes", 64L * 1024 * 1024),
            configuration.GetValue("Storage:MemoryCacheMaxFileBytes", 1024L * 1024));
    }

    /// <summary>Where the file with this id is (or would be) on disk.</summary>
    public string PathOf(Guid id)
    {
        var name = id.ToString("N");
        return Path.Combine(Root, name[^2..], name);
    }

    /// <summary>Writes the content of a new <paramref name="file"/> to disk, before its row is saved.</summary>
    public async Task WriteAsync(StoredFile file, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var path = PathOf(file.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(data, ct);
        // On disk before the row is committed, so a crash right after the commit can't lose it.
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The whole file, from memory when it is there.</summary>
    public async Task<byte[]> ReadAllBytesAsync(StoredFile file, CancellationToken ct) =>
        _cache.TryGetValue(file.Id, out byte[]? cached) ? cached! : await File.ReadAllBytesAsync(PathOf(file.Id), ct);

    /// <summary>
    /// The bytes of a file up to <c>Storage:MemoryCacheMaxFileBytes</c>, from memory (read and kept there on first use);
    /// null for a larger file, or when it is not on disk.
    /// </summary>
    public async Task<byte[]?> ReadSmallAsync(StoredFile file, CancellationToken ct)
    {
        if (file.Size > _maxCachedFileBytes) return null;
        if (_cache.TryGetValue(file.Id, out byte[]? cached)) return cached;
        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(PathOf(file.Id), ct);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        // Content never changes for an id, so entries only leave to make room or when the file is deleted.
        _cache.Set(file.Id, data, new MemoryCacheEntryOptions { Size = data.Length, SlidingExpiration = TimeSpan.FromHours(1) });
        return data;
    }

    /// <summary>Whether the file is on disk. Its row says it should be, so a missing one is logged as an error.</summary>
    public bool Exists(StoredFile file)
    {
        if (File.Exists(PathOf(file.Id))) return true;
        _logger.LogError("File {FileId} ({FileName}) is missing from {Root}", file.Id, file.FileName, Root);
        return false;
    }

    /// <summary>
    /// Deletes the rows that no image (deleted ones included, as they can be restored) and no user refers to any more,
    /// with their files, and then files on disk without a row that are older than a day. Returns how many files went.
    /// <paramref name="now"/> is the current time; tests move it forward.
    /// </summary>
    public async Task<int> SweepAsync(PrikBoardDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - UnreferencedGrace;
        // Raw SQL, so the soft-delete filter doesn't hide deleted images that still need their files.
        var unreferenced = await db.Database.SqlQuery<Guid>($"""
            DELETE FROM "StoredFiles" f
            WHERE f."CreatedAt" < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM "Images" i WHERE i."FileId" = f."Id")
              AND NOT EXISTS (SELECT 1 FROM "Principals" p WHERE p."AvatarFileId" = f."Id")
            RETURNING f."Id" AS "Value"
            """).ToListAsync(ct);
        foreach (var id in unreferenced) Delete(id);

        // An empty table more likely means a new, wrong database than a store without files: leave the disk alone.
        if (!await db.StoredFiles.AnyAsync(ct)) return unreferenced.Count;

        var strays = FilesOlderThan(now - StrayGrace);
        var known = new HashSet<Guid>();
        foreach (var chunk in strays.Keys.Chunk(1000))
        {
            known.UnionWith(await db.StoredFiles.Where(f => chunk.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct));
        }
        var orphans = strays.Keys.Where(id => !known.Contains(id)).ToList();
        if (orphans.Count > MaxStraysPerSweep)
        {
            _logger.LogError("Found {Count} files in {Root} without a row; deleting none, as that many suggests the wrong database",
                orphans.Count, Root);
            return unreferenced.Count;
        }
        foreach (var id in orphans) Delete(id);
        if (orphans.Count > 0) _logger.LogWarning("Deleted {Count} files without a row from {Root}", orphans.Count, Root);
        return unreferenced.Count + orphans.Count;
    }

    /// <summary>
    /// The files this store wrote that were last written before <paramref name="before"/>, by id. Anything else on the
    /// volume (lost+found, the write test) is left out.
    /// </summary>
    private Dictionary<Guid, string> FilesOlderThan(DateTimeOffset before)
    {
        var files = new Dictionary<Guid, string>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var shard = Path.GetFileName(folder);
            if (shard.Length != 2 || !shard.All(Uri.IsHexDigit)) continue;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(path);
                if (!Guid.TryParseExact(name, "N", out var id) || id.ToString("N") != name || !name.EndsWith(shard)) continue;
                if (File.GetLastWriteTimeUtc(path) < before.UtcDateTime) files[id] = path;
            }
        }
        return files;
    }

    private void Delete(Guid id)
    {
        _cache.Remove(id);
        try
        {
            File.Delete(PathOf(id));
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone, with its folder.
        }
    }

    public void Dispose() => _cache.Dispose();
}
