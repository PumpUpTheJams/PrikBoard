using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Tests;

/// <summary>Migrations that convert existing data, run against a throwaway database of their own.</summary>
public class MigrationTests : IAsyncLifetime
{
    private const string BeforeTagsAndAssignees = "20261018230734_AccessTokens";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    public Task InitializeAsync() => db.GetService<IMigrator>().MigrateAsync(BeforeTagsAndAssignees);

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Existing_tags_become_board_tags_and_assignee_names_are_linked_to_principals()
    {
        var (board, other) = (Guid.NewGuid(), Guid.NewGuid());
        var (mila, unnamed, outsider, agent) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (card, untouched) = (Guid.NewGuid(), Guid.NewGuid());
        var fallback = $"User {unnamed.ToString("N")[^4..]}";
        var assignees = $$"""
            [{"Name": "mila v.", "Kind": "Person"}, {"Name": "Outsider", "Kind": "Person"}, {"Name": "Claude Code", "Kind": "Agent"},
             {"Name": "Mila V.", "Kind": "Person"}, {"Name": "{{fallback}}", "Kind": "Person"}, {"Name": "Nobody", "Kind": "Person"}]
            """;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Boards" ("Id", "Name", "CreatedAt", "UpdatedAt") VALUES ({board}, 'Old', now(), now()), ({other}, 'Other', now(), now());
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Principals" ("Id", "Kind", "DisplayName", "CreatedAt", "LastSeenAt", "Issuer", "ClientId", "OwnerId") VALUES
                ({mila}, 'User', 'Mila V.', now(), now(), NULL, NULL, NULL),
                ({unnamed}, 'User', NULL, now(), now(), NULL, NULL, NULL),
                ({outsider}, 'User', 'Outsider', now(), now(), NULL, NULL, NULL),
                ({agent}, 'Agent', 'Claude Code', now(), now(), 'pat', {Guid.NewGuid().ToString()}, {mila});
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "BoardMembers" ("BoardId", "PrincipalId", "Role", "AddedAt") VALUES
                ({board}, {mila}, 'Owner', now()), ({board}, {unnamed}, 'Editor', now()), ({other}, {outsider}, 'Owner', now());
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AccessTokens" ("Id", "OwnerId", "AgentId", "TokenHash", "Hint", "MaxRole", "CreatedAt", "ExpiresAt")
            VALUES ({Guid.NewGuid()}, {mila}, {agent}, {new byte[32]}, 'abcd', 'Editor', now(), now() + interval '30 days');
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Notes" ("Id", "BoardId", "Title", "Color", "Tags", "Assignees", "Checklist", "Links", "AgentWorking", "X", "Y", "CreatedAt", "UpdatedAt")
            VALUES ({card}, {board}, 'Tagged', 'Butter', ARRAY['ui', 'backend'], {assignees}::jsonb, '[]', '[]', false, 0, 0, now(), now()),
                   ({untouched}, {other}, 'Plain', 'Butter', ARRAY['ui'], '[]', '[]', '[]', false, 0, 0, now(), now());
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        var tags = await db.BoardTags.OrderBy(t => t.BoardId == board ? 0 : 1).ThenBy(t => t.Name).Select(t => new { t.BoardId, t.Name }).ToListAsync();
        Assert.Equal([(board, "backend"), (board, "ui"), (other, "ui")], tags.Select(t => (t.BoardId, t.Name)));

        var linked = await db.Cards.Where(n => n.Id == card).Select(n => n.AssigneeIds).SingleAsync();
        // Mila keeps the place of her first mention ("mila v.") and her second is dropped, as are the outsider (not on this board) and "Nobody".
        // Her token's agent was linked too, until AgentsWorkForOwners gave its place to her, already there.
        Assert.Equal([mila, unnamed], linked);
        Assert.Empty(await db.Cards.Where(n => n.Id == untouched).Select(n => n.AssigneeIds).SingleAsync());
    }
}
