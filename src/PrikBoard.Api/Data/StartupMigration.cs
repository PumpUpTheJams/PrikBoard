using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace PrikBoard.Api.Data;

/// <summary>Brings the database up to date at startup.</summary>
public static class StartupMigration
{
    public static async Task RunAsync(PrikBoardDbContext db, CancellationToken ct)
    {
        // A first start finds no database or no history table yet. Npgsql then reads the history anyway and treats the
        // failed query as no migrations, but the failure is still logged as an error (as is a connection to a missing
        // database), so create both up front.
        var database = db.GetService<IRelationalDatabaseCreator>();
        if (!await database.ExistsAsync(ct)) await database.CreateAsync(ct);
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), ct);
        await db.Database.MigrateAsync(ct);
    }
}
