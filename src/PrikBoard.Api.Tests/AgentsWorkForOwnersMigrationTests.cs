using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Tests;

/// <summary>The AgentsWorkForOwners migration, run against a throwaway database of its own (see <see cref="MigrationTests"/>).</summary>
public class AgentsWorkForOwnersMigrationTests : IAsyncLifetime
{
    private const string WithAgentAssignees = "20261023100520_SignInProviders";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    public Task InitializeAsync() => db.GetService<IMigrator>().MigrateAsync(WithAgentAssignees);

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Token_agents_on_cards_give_way_to_their_owners_who_are_then_worked_for()
    {
        var (board, owner, other, tokenAgent, issuerAgent) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (working, both, issuer, untouched) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO "Boards" ("Id", "Name", "Key", "ShortId", "NextCardNumber", "IsPublic", "PublicShowsPeople", "CreatedAt", "UpdatedAt")
            VALUES ({{board}}, 'Agents', 'AGT', 'agt001', 5, false, false, now(), now());
            INSERT INTO "Principals" ("Id", "Kind", "DisplayName", "IsAdmin", "AvatarOptedOut", "CreatedAt", "LastSeenAt")
            VALUES ({{owner}}, 'User', 'Owner', false, false, now(), now()), ({{other}}, 'User', 'Other', false, false, now(), now());
            INSERT INTO "Principals" ("Id", "Kind", "DisplayName", "Issuer", "ClientId", "OwnerId", "CreatedAt", "LastSeenAt")
            VALUES ({{tokenAgent}}, 'Agent', 'Owner - Claude', 'pat', 'token', {{owner}}, now(), now()),
                   ({{issuerAgent}}, 'Agent', 'Build bot', 'ci', 'bot', NULL, now(), now());
            INSERT INTO "Cards" ("Id", "BoardId", "Number", "Title", "Color", "X", "Y", "AgentWorking", "Tags", "AssigneeIds", "Components", "CreatedAt", "UpdatedAt")
            VALUES ({{working}}, {{board}}, 1, 'Working', 'Butter', 0, 0, true, '{}', ARRAY[{{other}}, {{tokenAgent}}]::uuid[], '[]', now(), now()),
                   ({{both}}, {{board}}, 2, 'Both', 'Butter', 0, 0, false, '{}', ARRAY[{{tokenAgent}}, {{other}}, {{owner}}]::uuid[], '[]', now(), now()),
                   ({{issuer}}, {{board}}, 3, 'Issuer', 'Butter', 0, 0, true, '{}', ARRAY[{{issuerAgent}}]::uuid[], '[]', now(), now()),
                   ({{untouched}}, {{board}}, 4, 'People', 'Butter', 0, 0, false, '{}', ARRAY[{{other}}]::uuid[], '[]', now(), now());
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        var cards = await db.Cards.AsNoTracking().ToDictionaryAsync(n => n.Id);
        // The owner takes the agent's place and is the one worked for.
        Assert.Equal([other, owner], cards[working].AssigneeIds);
        Assert.Equal(owner, cards[working].AgentWorkingForId);
        // Once, where the first of them was.
        Assert.Equal([owner, other], cards[both].AssigneeIds);
        Assert.Null(cards[both].AgentWorkingForId);
        // An agent without an owner stays, and works for itself.
        Assert.Equal([issuerAgent], cards[issuer].AssigneeIds);
        Assert.Equal(issuerAgent, cards[issuer].AgentWorkingForId);
        Assert.Equal([other], cards[untouched].AssigneeIds);
    }
}
