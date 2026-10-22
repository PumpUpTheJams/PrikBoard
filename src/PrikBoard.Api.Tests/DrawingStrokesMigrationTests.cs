using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The DrawingStrokes migration, run against a throwaway database of its own (see <see cref="MigrationTests"/>).</summary>
public class DrawingStrokesMigrationTests : IAsyncLifetime
{
    private const string WithSingleStrokes = "20261020202830_AddDrawings";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    public Task InitializeAsync() => db.GetService<IMigrator>().MigrateAsync(WithSingleStrokes);

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task A_single_stroke_drawing_becomes_a_drawing_of_one_stroke_with_its_ink_and_width()
    {
        var (board, drawing) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Boards" ("Id", "Name", "CreatedAt", "UpdatedAt") VALUES ({board}, 'Doodles', now(), now());
            INSERT INTO "Drawings" ("Id", "BoardId", "X", "Y", "Width", "Height", "Color", "StrokeWidth", "PointCount", "CreatedAt", "UpdatedAt")
            VALUES ({drawing}, {board}, 40, 60, 35, 25, 'Brick', 8, 2, now(), now());
            INSERT INTO "DrawingContents" ("DrawingId", "Points") VALUES ({drawing}, '[[10,10,0.5],[30.5,20,0.75]]');
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        var migrated = await db.Drawings.AsNoTracking().Include(d => d.Content).SingleAsync(d => d.Id == drawing);
        Assert.Equal([InkColor.Brick], migrated.Colors);
        Assert.Equal((40, 60, 0, 2), (migrated.X, migrated.Y, migrated.Revision, migrated.PointCount));
        var stroke = Assert.Single(migrated.Content!.Strokes);
        Assert.Equal(8, stroke.StrokeWidth);
        Assert.Equal([[10, 10, 0.5], [30.5, 20, 0.75]], stroke.Points);
    }
}
