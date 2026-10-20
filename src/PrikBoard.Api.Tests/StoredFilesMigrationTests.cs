using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Tests;

/// <summary>
/// The move of image and avatar bytes from the database to disk (<see cref="StartupMigration"/>), run against a throwaway
/// database and folder of its own (see <see cref="MigrationTests"/>).
/// </summary>
public class StoredFilesMigrationTests : IAsyncLifetime
{
    private const string BeforeStoredFiles = "20261019220831_TagColors";
    private const string StoredFiles = "20261020174830_StoredFiles";
    private const string DropDatabaseBlobs = "20261020175303_DropDatabaseBlobs";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    private readonly FileStore files = new(Path.Combine(Path.GetTempPath(), $"prikboard_tests_{Guid.NewGuid():N}"), NullLogger.Instance);

    private readonly Guid board = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await db.GetService<IMigrator>().MigrateAsync(BeforeStoredFiles);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Boards" ("Id", "Name", "CreatedAt", "UpdatedAt") VALUES ({board}, 'Old', now(), now());
            """);
    }

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
        files.Dispose();
        Directory.Delete(files.Root, recursive: true);
    }

    [Fact]
    public async Task Image_and_avatar_bytes_move_from_the_database_into_files_on_disk()
    {
        var (live, deleted, user) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (png, gif) = (TestImages.Png(300, 200), TestImages.Gif(40, 40));
        var addedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var avatarAt = addedAt.AddDays(1);
        await AddImageAsync(live, "ingest-errors.png", "image/png", png, addedAt);
        await AddImageAsync(deleted, "throttle.gif", "image/gif", gif, addedAt, deletedAt: addedAt.AddHours(1));
        await AddUserAsync(user, avatarAt);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "UserAvatars" ("UserId", "ContentType", "Data") VALUES ({user}, 'image/png', {PrikBoardApiFactory.TestAvatar});
            """);

        await StartupMigration.RunAsync(db, files, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Null(await Scalar<string?>("""SELECT to_regclass('"ImageContents"')::text AS "Value" """));
        Assert.Null(await Scalar<string?>("""SELECT to_regclass('"UserAvatars"')::text AS "Value" """));
        Assert.Equal(0, await Scalar<int>(
            """SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'Images' AND column_name = 'FileName'"""));

        var images = await db.Images.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).Include(i => i.File).ToDictionaryAsync(i => i.Id);
        AssertMoved(images[live].File!, "ingest-errors.png", "image/png", png, addedAt, Path.Combine("boards", board.ToString()));
        // A deleted image can still be restored, so it needs its file too.
        AssertMoved(images[deleted].File!, "throttle.gif", "image/gif", gif, addedAt, Path.Combine("boards", board.ToString()));
        var avatar = await db.Users.Where(u => u.Id == user).Select(u => u.AvatarFile).SingleAsync();
        AssertMoved(avatar!, "avatar.png", "image/png", PrikBoardApiFactory.TestAvatar, avatarAt, "avatars");
    }

    [Fact]
    public async Task A_move_that_stopped_halfway_finishes_without_copying_twice()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await AddImageAsync(first, "first.png", "image/png", TestImages.Png(10, 10), DateTimeOffset.UtcNow);
        await AddImageAsync(second, "second.png", "image/png", TestImages.Png(20, 20), DateTimeOffset.UtcNow);
        // As if the first move stopped after the first image.
        await db.GetService<IMigrator>().MigrateAsync(StoredFiles);
        var done = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "StoredFiles" ("Id", "FileName", "ContentType", "Size", "Sha256", "CreatedAt")
            VALUES ({done}, 'first.png', 'image/png', 33, {new byte[32]}, now());
            """);
        await db.Database.ExecuteSqlAsync($"""UPDATE "Images" SET "FileId" = {done} WHERE "Id" = {first};""");

        await StartupMigration.RunAsync(db, files, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, await db.StoredFiles.CountAsync());
        Assert.Equal(done, await db.Images.Where(i => i.Id == first).Select(i => i.FileId).SingleAsync());
        var moved = await db.Images.Where(i => i.Id == second).Select(i => i.File).SingleAsync();
        Assert.Equal(TestImages.Png(20, 20), await File.ReadAllBytesAsync(files.PathOf(moved!)));
    }

    [Fact]
    public async Task Migrating_without_moving_the_bytes_first_stops_before_dropping_them()
    {
        var image = Guid.NewGuid();
        await AddImageAsync(image, "kept.png", "image/png", TestImages.Png(10, 10), DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync());

        Assert.DoesNotContain(DropDatabaseBlobs, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await Scalar<int>("""SELECT count(*)::int AS "Value" FROM "ImageContents" """));

        // Starting the API moves them and finishes the job.
        await StartupMigration.RunAsync(db, files, NullLogger.Instance, CancellationToken.None);
        var file = await db.Images.Where(i => i.Id == image).Select(i => i.File).SingleAsync();
        Assert.True(File.Exists(files.PathOf(file!)));
    }

    [Fact]
    public async Task Images_without_bytes_and_avatar_versions_without_a_picture_are_dropped_by_the_move()
    {
        var (image, user) = (Guid.NewGuid(), Guid.NewGuid());
        await AddImageAsync(image, "empty.png", "image/png", null, DateTimeOffset.UtcNow);
        await AddUserAsync(user, DateTimeOffset.UtcNow);

        await StartupMigration.RunAsync(db, files, NullLogger.Instance, CancellationToken.None);

        Assert.False(await db.Images.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).AnyAsync(i => i.Id == image));
        Assert.Null(await db.Users.Where(u => u.Id == user).Select(u => u.AvatarUpdatedAt).SingleAsync());
    }

    private void AssertMoved(Domain.StoredFile file, string fileName, string contentType, byte[] data, DateTimeOffset createdAt, string folder)
    {
        Assert.Equal((fileName, contentType, data.Length, createdAt), (file.FileName, file.ContentType, file.Size, file.CreatedAt));
        Assert.Equal(SHA256.HashData(data), file.Sha256);
        // In its board's folder (or the avatars'), named after the row, not after what is in it.
        var path = Path.Combine(files.Root, folder, file.Id.ToString());
        Assert.Equal(path, files.PathOf(file));
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    private async Task AddImageAsync(Guid id, string fileName, string contentType, byte[]? data, DateTimeOffset createdAt,
        DateTimeOffset? deletedAt = null)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Images" ("Id", "BoardId", "FileName", "ContentType", "Size", "PixelWidth", "PixelHeight", "X", "Y", "Width", "Height", "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES ({id}, {board}, {fileName}, {contentType}, {data?.Length ?? 0}, 10, 10, 0, 0, 80, 80, {createdAt}, {createdAt}, {deletedAt});
            """);
        if (data is not null)
        {
            await db.Database.ExecuteSqlAsync($"""INSERT INTO "ImageContents" ("ImageId", "Data") VALUES ({id}, {data});""");
        }
    }

    private Task AddUserAsync(Guid id, DateTimeOffset avatarUpdatedAt) => db.Database.ExecuteSqlAsync($"""
        INSERT INTO "Principals" ("Id", "Kind", "CreatedAt", "LastSeenAt", "IsAdmin", "AvatarOptedOut", "AvatarUpdatedAt")
        VALUES ({id}, 'User', now(), now(), false, false, {avatarUpdatedAt});
        """);

    private Task<T> Scalar<T>(string sql) => db.Database.SqlQueryRaw<T>(sql).SingleAsync();
}
