using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The ReadableIds migration, run against a throwaway database of its own (see <see cref="MigrationTests"/>).</summary>
public class ReadableIdsMigrationTests : IAsyncLifetime
{
    private const string BeforeReadableIds = "20261020202830_AddDrawings";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    public Task InitializeAsync() => db.GetService<IMigrator>().MigrateAsync(BeforeReadableIds);

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Boards_get_short_ids_and_keys_notes_get_numbers_and_links_become_paths()
    {
        var (board, other) = (Guid.NewGuid(), Guid.NewGuid());
        var (first, deleted, linking, away, missing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var blocks = $$"""
            [{"id": "{{Guid.NewGuid()}}", "kind": "Links", "x": 20, "y": 20, "width": 360, "height": 160, "title": null, "text": null, "items": [],
              "links": [{"title": "Web", "url": "https://example.com"},
                        {"title": "First", "url": "/#/boards/{{board}}/notes/{{first}}"},
                        {"title": "Deleted", "url": "/#/boards/{{board}}/notes/{{deleted.ToString().ToUpperInvariant()}}"},
                        {"title": "Away", "url": "/#/boards/{{other}}/notes/{{away}}"},
                        {"title": "Missing", "url": "/#/boards/{{board}}/notes/{{missing}}"}]},
             {"id": "{{Guid.NewGuid()}}", "kind": "Text", "x": 400, "y": 20, "width": 340, "height": 220, "title": null,
              "text": "/#/boards/{{board}}/notes/{{first}}", "items": [], "links": []}]
            """;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Boards" ("Id", "Name", "CreatedAt", "UpdatedAt") VALUES ({board}, 'MCP me some tickets', now(), now()), ({other}, 'X', now(), now());
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Notes" ("Id", "BoardId", "Title", "Color", "Tags", "AssigneeIds", "Blocks", "AgentWorking", "X", "Y", "CreatedAt", "UpdatedAt", "DeletedAt")
            VALUES ({linking}, {board}, 'Linking', 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], {blocks}::jsonb, false, 0, 0, now() + interval '2 seconds', now(), NULL),
                   ({first}, {board}, 'First', 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], '[]', false, 0, 0, now(), now(), NULL),
                   ({deleted}, {board}, 'Deleted', 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], '[]', false, 0, 0, now() + interval '1 second', now(), now()),
                   ({away}, {other}, 'Away', 'Butter', ARRAY[]::text[], ARRAY[]::uuid[], '[]', false, 0, 0, now(), now(), NULL);
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        var boards = await db.Boards.AsNoTracking().ToDictionaryAsync(b => b.Id);
        Assert.Equal(("MMST", 4), (boards[board].Key, boards[board].NextNoteNumber));
        Assert.Equal(("PB", 2), (boards[other].Key, boards[other].NextNoteNumber));
        Assert.All(boards.Values, b => Assert.True(Board.IsShortId(b.ShortId), b.ShortId));
        Assert.NotEqual(boards[board].ShortId, boards[other].ShortId);

        var notes = await db.Notes.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete]).AsNoTracking().ToDictionaryAsync(n => n.Id);
        // In the order they were created, deleted ones too.
        Assert.Equal([1, 2, 3, 1], new[] { first, deleted, linking, away }.Select(id => notes[id].Number));

        var path = (Guid board, Guid note) => NoteLinks.NotePath(boards[board].ShortId, notes[note].Number);
        Assert.Equal(
            ["https://example.com", path(board, first), path(board, deleted), path(other, away), $"/#/boards/{board}/notes/{missing}"],
            notes[linking].Blocks[0].Links.Select(l => l.Url));
        Assert.Equal(["Web", "First", "Deleted", "Away", "Missing"], notes[linking].Blocks[0].Links.Select(l => l.Title));
        // Only links change: text that looks like a path stays.
        Assert.Equal($"/#/boards/{board}/notes/{first}", notes[linking].Blocks[1].Text);
    }
}
