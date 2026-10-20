using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using PrikBoard.Api.Data.Migrations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Brings the database up to date at startup. Image and avatar bytes used to live in the database; the
/// <see cref="StoredFiles"/> migration adds the file rows, the bytes are then moved to the <see cref="FileStore"/>, and only
/// then does <see cref="DropDatabaseBlobs"/> drop the old tables (it refuses while anything is left).
/// </summary>
public static class StartupMigration
{
    /// <summary>Rows moved at a time; images are at most 10 MB, so this bounds the memory a batch takes.</summary>
    private const int BatchSize = 10;

    public static async Task RunAsync(PrikBoardDbContext db, FileStore files, ILogger logger, CancellationToken ct)
    {
        var applied = await db.Database.CanConnectAsync(ct) ? (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet() : [];
        // Never once DropDatabaseBlobs is applied: migrating to an earlier target would revert the migrations after it.
        if (!applied.Contains(IdOf<DropDatabaseBlobs>()))
        {
            await db.Database.MigrateAsync(IdOf<StoredFiles>(), ct);
            await MoveDatabaseBlobsAsync(db, files, logger, ct);
        }
        await db.Database.MigrateAsync(ct);
    }

    private static string IdOf<T>() where T : Migration => typeof(T).GetCustomAttribute<MigrationAttribute>()!.Id;

    /// <summary>
    /// Writes each image and avatar in the old tables to disk, then adds its file row and points the image or user at it in
    /// one transaction. Rows still without a file are what is left, so a move that stopped halfway carries on where it was.
    /// Raw SQL throughout: the model no longer has the old tables, and its query filter would skip deleted images.
    /// </summary>
    private static async Task MoveDatabaseBlobsAsync(PrikBoardDbContext db, FileStore files, ILogger logger, CancellationToken ct)
    {
        var images = 0;
        while (true)
        {
            var batch = await db.Database.SqlQuery<LegacyImage>($"""
                SELECT i."Id", i."FileName", i."ContentType", i."CreatedAt", c."Data"
                FROM "Images" i JOIN "ImageContents" c ON c."ImageId" = i."Id"
                WHERE i."FileId" IS NULL
                ORDER BY i."Id"
                LIMIT {BatchSize}
                """).ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var image in batch)
            {
                var file = StoredFile.For(image.FileName, image.ContentType, image.Data, image.CreatedAt);
                await SaveAsync(db, files, file, image.Data, $"""UPDATE "Images" SET "FileId" = {file.Id} WHERE "Id" = {image.Id}""", ct);
            }
            images += batch.Count;
            logger.LogInformation("Moved {Count} images from the database to {Root}", images, files.Root);
        }

        var avatars = 0;
        while (true)
        {
            var batch = await db.Database.SqlQuery<LegacyAvatar>($"""
                SELECT a."UserId", a."ContentType", p."AvatarUpdatedAt", a."Data"
                FROM "UserAvatars" a JOIN "Principals" p ON p."Id" = a."UserId"
                WHERE p."AvatarFileId" IS NULL
                ORDER BY a."UserId"
                LIMIT {BatchSize}
                """).ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var avatar in batch)
            {
                var file = StoredFile.For($"avatar.{ImageFormats.ExtensionOf(avatar.ContentType)}", avatar.ContentType, avatar.Data,
                    avatar.AvatarUpdatedAt);
                await SaveAsync(db, files, file, avatar.Data,
                    $"""UPDATE "Principals" SET "AvatarFileId" = {file.Id} WHERE "Id" = {avatar.UserId}""", ct);
            }
            avatars += batch.Count;
            logger.LogInformation("Moved {Count} avatars from the database to {Root}", avatars, files.Root);
        }

        // Images without bytes could never be downloaded, and can't get a file either.
        var empty = await db.Database.ExecuteSqlAsync($"""DELETE FROM "Images" WHERE "FileId" IS NULL""", ct);
        if (empty > 0) logger.LogWarning("Deleted {Count} images that had no bytes in the database", empty);
        // Likewise an avatar version without a picture: those people keep showing their initials.
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "Principals" SET "AvatarUpdatedAt" = NULL WHERE "AvatarUpdatedAt" IS NOT NULL AND "AvatarFileId" IS NULL""", ct);
    }

    /// <summary>Writes the file, then adds its row and runs <paramref name="refer"/>, which points its owner at it.</summary>
    private static async Task SaveAsync(
        PrikBoardDbContext db, FileStore files, StoredFile file, byte[] data, FormattableString refer, CancellationToken ct)
    {
        await files.WriteAsync(file, data, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "StoredFiles" ("Id", "FileName", "ContentType", "Size", "Sha256", "CreatedAt")
            VALUES ({file.Id}, {file.FileName}, {file.ContentType}, {file.Size}, {file.Sha256}, {file.CreatedAt})
            """, ct);
        await db.Database.ExecuteSqlAsync(refer, ct);
        await transaction.CommitAsync(ct);
    }

    private record LegacyImage(Guid Id, string FileName, string ContentType, DateTimeOffset CreatedAt, byte[] Data);

    private record LegacyAvatar(Guid UserId, string ContentType, DateTimeOffset? AvatarUpdatedAt, byte[] Data);
}
