using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Keeps the contents of <see cref="StoredFile"/>s (images, avatars) on disk under <c>Storage:Path</c>, which is a volume
/// of its own in production: a board's files in <c>boards/{boardId}/</c>, avatars in <c>avatars/</c>. A file is named
/// after its id, so names on disk say nothing about what is in them; the original name is in the row. Small files are
/// kept in memory as well, up to <c>Storage:MemoryCacheBytes</c> in all.
/// </summary>
/// <remarks>
/// The file is written before its row is committed, and a row is only inserted together with the row that refers to it.
/// A failed save or a crash therefore leaves at most a file without a row, never a row without a file. <see cref="SweepAsync"/>
/// deletes both rows nothing refers to (deleted boards and users, purged images, replaced or removed avatars) and such stray files.
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

    /// <summary>Holds a folder per board, named after its id, with that board's files.</summary>
    private const string BoardsFolder = "boards";

    /// <summary>Holds the files that belong to no board: avatars.</summary>
    private const string AvatarsFolder = "avatars";

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

    /// <summary>Where the file is (or would be) on disk.</summary>
    public string PathOf(StoredFile file) => PathOf(file.BoardId, file.Id);

    private string PathOf(Guid? boardId, Guid id) => boardId is { } board
        ? Path.Combine(Root, BoardsFolder, board.ToString(), id.ToString())
        : Path.Combine(Root, AvatarsFolder, id.ToString());

    /// <summary>Writes the content of a new <paramref name="file"/> to disk, before its row is saved.</summary>
    public async Task WriteAsync(StoredFile file, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var path = PathOf(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(data, ct);
        // On disk before the row is committed, so a crash right after the commit can't lose it.
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The whole file, from memory when it is there.</summary>
    public async Task<byte[]> ReadAllBytesAsync(StoredFile file, CancellationToken ct) =>
        _cache.TryGetValue(file.Id, out byte[]? cached) ? cached! : await File.ReadAllBytesAsync(PathOf(file), ct);

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
            data = await File.ReadAllBytesAsync(PathOf(file), ct);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        // Content never changes for an id, so entries never go stale: they leave to make room, after an hour unused, or
        // when the file is deleted.
        _cache.Set(file.Id, data, new MemoryCacheEntryOptions { Size = data.Length, SlidingExpiration = TimeSpan.FromHours(1) });
        return data;
    }

    /// <summary>Whether the file is on disk. Its row says it should be, so a missing one is logged as an error.</summary>
    public bool Exists(StoredFile file)
    {
        if (File.Exists(PathOf(file))) return true;
        _logger.LogError("File {FileId} ({FileName}) is missing from {Root}", file.Id, file.FileName, Root);
        return false;
    }

    /// <summary>
    /// Deletes the rows that no image (deleted ones included, as they can be restored) and no user refers to any more,
    /// with their files, and then files on disk without a row that are older than a day, and board folders left empty for a
    /// day. Returns how many files went. <paramref name="now"/> is the current time; tests move it forward.
    /// </summary>
    public async Task<int> SweepAsync(PrikBoardDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - UnreferencedGrace;
        // Raw SQL, so the soft-delete filter doesn't hide deleted images that still need their files.
        var unreferenced = await db.Database.SqlQuery<SweptFile>($"""
            DELETE FROM "StoredFiles" f
            WHERE f."CreatedAt" < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM "Images" i WHERE i."FileId" = f."Id")
              AND NOT EXISTS (SELECT 1 FROM "Principals" p WHERE p."AvatarFileId" = f."Id")
            RETURNING f."Id", f."BoardId"
            """).ToListAsync(ct);
        foreach (var file in unreferenced) Delete(file.Id, PathOf(file.BoardId, file.Id));

        // An empty table more likely means a new, wrong database than a store without files: leave the disk alone.
        if (!await db.StoredFiles.AnyAsync(ct)) return unreferenced.Count;

        var before = now - StrayGrace;
        var strays = FilesOlderThan(before);
        var known = new HashSet<Guid>();
        foreach (var chunk in strays.Keys.Chunk(1000))
        {
            known.UnionWith(await db.StoredFiles.Where(f => chunk.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct));
        }
        var orphans = strays.Where(stray => !known.Contains(stray.Key)).ToList();
        if (orphans.Count > MaxStraysPerSweep)
        {
            _logger.LogError("Found {Count} files in {Root} without a row; deleting none, as that many suggests the wrong database",
                orphans.Count, Root);
            return unreferenced.Count;
        }
        foreach (var (id, path) in orphans) Delete(id, path);
        if (orphans.Count > 0) _logger.LogWarning("Deleted {Count} files without a row from {Root}", orphans.Count, Root);

        DeleteEmptyBoardFolders(before);
        return unreferenced.Count + orphans.Count;
    }

    private record SweptFile(Guid Id, Guid? BoardId);

    /// <summary>
    /// The files this store wrote that were last written before <paramref name="before"/>, by id. Anything else on the
    /// volume (lost+found, the write test, names that aren't ids) is left out.
    /// </summary>
    private Dictionary<Guid, string> FilesOlderThan(DateTimeOffset before)
    {
        var files = new Dictionary<Guid, string>();
        foreach (var folder in BoardFolders().Append(Path.Combine(Root, AvatarsFolder)).Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (IsId(Path.GetFileName(path), out var id) && File.GetLastWriteTimeUtc(path) < before.UtcDateTime) files[id] = path;
            }
        }
        return files;
    }

    /// <summary>The folders in <c>boards/</c> that are named after a board id.</summary>
    private IEnumerable<string> BoardFolders()
    {
        var boards = Path.Combine(Root, BoardsFolder);
        return Directory.Exists(boards) ? Directory.EnumerateDirectories(boards).Where(f => IsId(Path.GetFileName(f), out _)) : [];
    }

    /// <summary>
    /// Deletes the folders of boards whose files are all gone, once nothing has changed in them since
    /// <paramref name="before"/>: a newer one may be about to get the first file of a new board.
    /// </summary>
    private void DeleteEmptyBoardFolders(DateTimeOffset before)
    {
        foreach (var folder in BoardFolders().ToList())
        {
            if (Directory.GetLastWriteTimeUtc(folder) >= before.UtcDateTime || Directory.EnumerateFileSystemEntries(folder).Any()) continue;
            try
            {
                Directory.Delete(folder);
            }
            catch (IOException)
            {
                // Something arrived in the meantime.
            }
        }
    }

    private static bool IsId(string name, out Guid id) => Guid.TryParseExact(name, "D", out id) && id.ToString() == name;

    private void Delete(Guid id, string path)
    {
        _cache.Remove(id);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone, with its folder.
        }
    }

    public void Dispose() => _cache.Dispose();
}
