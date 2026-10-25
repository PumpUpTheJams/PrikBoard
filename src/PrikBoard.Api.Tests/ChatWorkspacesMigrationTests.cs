using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The ChatWorkspaces migration, run against a throwaway database of its own (see <see cref="MigrationTests"/>).</summary>
public class ChatWorkspacesMigrationTests : IAsyncLifetime
{
    private const string WithDiscordTables = "20261023185940_CardIndexAndAttachmentChecks";

    private readonly PrikBoardDbContext db = new(new DbContextOptionsBuilder<PrikBoardDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("PRIKBOARD_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=prikboard;Password=prikboard")
        {
            Database = $"prikboard_tests_{Guid.NewGuid():N}",
        }.ConnectionString)
        .Options);

    public Task InitializeAsync() => db.GetService<IMigrator>().MigrateAsync(WithDiscordTables);

    public async Task DisposeAsync()
    {
        await db.Database.EnsureDeletedAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Discord_rows_move_to_the_chat_tables_and_back()
    {
        var (board, owner, agent, card, bucket) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var hash = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var channels = $$"""[{"id": "1300000000000000001", "name": "general", "updates": 3, "saveBucketId": "{{bucket}}"}]""";
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO "Boards" ("Id", "Name", "Key", "ShortId", "NextCardNumber", "IsPublic", "PublicShowsPeople", "CreatedAt", "UpdatedAt")
            VALUES ({{board}}, 'Night owls', 'OWL', 'owl001', 1, false, false, now(), now());
            INSERT INTO "Principals" ("Id", "Kind", "DisplayName", "IsAdmin", "AvatarOptedOut", "CreatedAt", "LastSeenAt")
            VALUES ({{owner}}, 'User', 'Owner', false, false, now(), now());
            INSERT INTO "Principals" ("Id", "Kind", "DisplayName", "Issuer", "ClientId", "CreatedAt", "LastSeenAt")
            VALUES ({{agent}}, 'Agent', 'Night owls server', 'discord', '1200000000000000001', now(), now());
            INSERT INTO "DiscordGuilds" ("GuildId", "AgentId", "ConnectedById", "ConnectedAt")
            VALUES ('1200000000000000001', {{agent}}, {{owner}}, now());
            INSERT INTO "DiscordBoardChannels" ("GuildId", "BoardId", "WholeServer", "Channels")
            VALUES ('1200000000000000001', {{board}}, true, {{channels}}::jsonb);
            INSERT INTO "DiscordAccounts" ("DiscordUserHash", "UserId", "LinkedAt") VALUES ({{hash}}, {{owner}}, now());
            INSERT INTO "DiscordPreviewMessages" ("MessageId", "GuildId", "ChannelId", "Sources", "CardIds", "BoardIds", "PostedAt")
            VALUES ('1400000000000000001', '1200000000000000001', '1300000000000000001', ARRAY['OWL-1'], ARRAY[{{card}}], ARRAY[{{board}}], now());
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        var workspace = await db.ChatWorkspaces.AsNoTracking().SingleAsync();
        Assert.Equal((ChatPlatform.Discord, "1200000000000000001", agent, owner),
            (workspace.Platform, workspace.WorkspaceId, workspace.AgentId, workspace.ConnectedById));
        var limit = await db.ChatBoardChannels.AsNoTracking().SingleAsync();
        Assert.Equal((ChatPlatform.Discord, "1200000000000000001", board, true), (limit.Platform, limit.WorkspaceId, limit.BoardId, limit.WholeWorkspace));
        Assert.Equal([new ChatChannel("1300000000000000001", "general", ChatUpdates.NewCards | ChatUpdates.Moves, bucket)], limit.Channels);
        var account = await db.ChatAccounts.AsNoTracking().SingleAsync();
        Assert.Equal((ChatPlatform.Discord, owner), (account.Platform, account.UserId));
        Assert.Equal(hash, account.UserHash);
        var message = await db.ChatPreviewMessages.AsNoTracking().SingleAsync();
        Assert.Equal((ChatPlatform.Discord, "1200000000000000001", "1300000000000000001", "1400000000000000001"),
            (message.Platform, message.WorkspaceId, message.ChannelId, message.MessageId));
        Assert.Equal(["OWL-1"], message.Sources);
        Assert.Equal([card], message.CardIds);
        Assert.Equal([board], message.BoardIds);

        // Rolling back keeps the Discord rows; a Slack workspace has nowhere to go.
        var slackAgent = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO "Principals" ("Id", "Kind", "DisplayName", "Issuer", "ClientId", "CreatedAt", "LastSeenAt")
            VALUES ({{slackAgent}}, 'Agent', 'Acme', 'slack', 'T0123ABCD', now(), now());
            INSERT INTO "ChatWorkspaces" ("Platform", "WorkspaceId", "AgentId", "ConnectedAt") VALUES ('Slack', 'T0123ABCD', {{slackAgent}}, now());
            """);
        await db.GetService<IMigrator>().MigrateAsync(WithDiscordTables);

        Assert.Equal(["1200000000000000001"], await db.Database.SqlQuery<string>($"""SELECT "GuildId" AS "Value" FROM "DiscordGuilds" """).ToListAsync());
        Assert.Equal([true], await db.Database.SqlQuery<bool>($"""SELECT "WholeServer" AS "Value" FROM "DiscordBoardChannels" """).ToListAsync());
        Assert.Equal([owner], await db.Database.SqlQuery<Guid>($"""SELECT "UserId" AS "Value" FROM "DiscordAccounts" """).ToListAsync());
        Assert.Equal(["1400000000000000001"], await db.Database.SqlQuery<string>($"""SELECT "MessageId" AS "Value" FROM "DiscordPreviewMessages" """).ToListAsync());
    }
}
