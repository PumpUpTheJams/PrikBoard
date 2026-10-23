using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The NoteBlocks migration, which turns a note's body, checklist and links into blocks (now card components), run against a throwaway database of its own (see <see cref="MigrationTests"/>).</summary>
public class CardComponentMigrationTests : IAsyncLifetime
{
    private const string BeforeBlocks = "20261019010826_BoardRules";
    /// <summary>Stops before StoredFiles: the images here have no bytes, so the move of bytes to disk (StartupMigration) would drop them.</summary>
    private const string BeforeStoredFiles = "20261019220831_TagColors";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    public Task InitializeAsync() => db.GetService<IMigrator>().MigrateAsync(BeforeBlocks);

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Body_checklist_and_links_become_components_and_attached_images_get_a_place()
    {
        var board = Guid.NewGuid();
        var (full, listsOnly, empty) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        const string checklist = """[{"Text": "Backoff", "Done": true}, {"Text": "DLQ", "Done": false}]""";
        const string links = """[{"Title": "PR #412", "Url": "https://example.com/412"}]""";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Boards" ("Id", "Name", "CreatedAt", "UpdatedAt") VALUES ({board}, 'Old', now(), now());
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Notes" ("Id", "BoardId", "Title", "Body", "Color", "Tags", "AssigneeIds", "Checklist", "Links", "AgentWorking", "X", "Y", "CreatedAt", "UpdatedAt")
            VALUES ({full}, {board}, 'Full', 'Retry tiles', 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], {checklist}::jsonb, {links}::jsonb, false, 0, 0, now(), now()),
                   ({listsOnly}, {board}, 'Lists', NULL, 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], {checklist}::jsonb, '[]', false, 0, 0, now(), now()),
                   ({empty}, {board}, 'Empty', NULL, 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], '[]', NULL, false, 0, 0, now(), now());
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Images" ("Id", "BoardId", "NoteId", "FileName", "ContentType", "Size", "PixelWidth", "PixelHeight", "X", "Y", "Width", "Height", "CreatedAt", "UpdatedAt")
            VALUES ({first}, {board}, {full}, 'a.png', 'image/png', 1, 300, 200, 0, 0, 300, 200, now(), now()),
                   ({second}, {board}, {full}, 'b.png', 'image/png', 1, 200, 200, 0, 0, 200, 200, now() + interval '1 second', now());
            """);

        await db.GetService<IMigrator>().MigrateAsync(BeforeStoredFiles);

        // Still the Notes table and its Blocks column this far into the migrations (CardsAndComponents renames them).
        var cards = (await db.Database.SqlQuery<OldCard>($"""SELECT "Id", "Blocks"::text AS "Blocks" FROM "Notes" """).ToListAsync())
            .ToDictionary(n => n.Id, n => new { n.Id, Components = CardComponentsJson.Converter.ConvertFromProviderTyped(n.Blocks) });
        var components = cards[full].Components;
        Assert.Equal([CardComponentKind.Text, CardComponentKind.Checklist, CardComponentKind.Links], components.Select(b => b.Kind));
        Assert.Equal([(20, 20, 340, 220), (400, 20, 340, 200), (780, 20, 360, 160)], components.Select(b => (b.X, b.Y, b.Width, b.Height)));
        Assert.Equal("Retry tiles", components[0].Text);
        Assert.Equal([("Backoff", true), ("DLQ", false)], components[1].Items.Select(i => (i.Text, i.Done)));
        Assert.Equal([("PR #412", "https://example.com/412")], components[2].Links.Select(l => (l.Title, l.Url)));
        Assert.Equal(3, components.Select(b => b.Id).Distinct().Count(id => id != Guid.Empty));

        var listComponent = Assert.Single(cards[listsOnly].Components);
        Assert.Equal((CardComponentKind.Checklist, 20), (listComponent.Kind, listComponent.X));
        Assert.Empty(cards[empty].Components);

        var images = await db.Images.AsNoTracking().OrderBy(i => i.CreatedAt).Select(i => new { i.X, i.Y }).ToListAsync();
        // Under the tallest component (20 + 220) with a 20 px gap, side by side.
        Assert.Equal([(20, 260), (340, 260)], images.Select(i => (i.X, i.Y)));
    }

    private sealed record OldCard(Guid Id, string Blocks);
}
