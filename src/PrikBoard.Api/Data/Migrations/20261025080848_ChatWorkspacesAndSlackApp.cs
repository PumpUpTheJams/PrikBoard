using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <summary>
    /// The Slack integration (docs/slack.md). The Discord tables become the shared chat model: DiscordGuilds,
    /// DiscordBoardChannels, DiscordAccounts and DiscordPreviewMessages move into ChatWorkspaces, ChatBoardChannels,
    /// ChatAccounts and ChatPreviewMessages with Platform 'Discord' (new tables rather than renames, so keys and constraint
    /// names match the model). SlackAppSettings keeps the Slack app's tokens and address.
    /// </summary>
    public partial class ChatWorkspacesAndSlackApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatAccounts",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UserHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAccounts", x => new { x.Platform, x.UserHash });
                    table.ForeignKey(
                        name: "FK_ChatAccounts_Principals_UserId",
                        column: x => x.UserId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatPreviewMessages",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Sources = table.Column<List<string>>(type: "text[]", nullable: false),
                    CardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    BoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatPreviewMessages", x => new { x.Platform, x.ChannelId, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "ChatWorkspaces",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatWorkspaces", x => new { x.Platform, x.WorkspaceId });
                    table.ForeignKey(
                        name: "FK_ChatWorkspaces_Principals_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatWorkspaces_Principals_ConnectedById",
                        column: x => x.ConnectedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SlackAppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ProtectedBotToken = table.Column<string>(type: "text", nullable: true),
                    ProtectedAppToken = table.Column<string>(type: "text", nullable: true),
                    TeamId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TeamName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BotName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PublicUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackAppSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlackAppSettings_Principals_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChatBoardChannels",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WholeWorkspace = table.Column<bool>(type: "boolean", nullable: false),
                    Channels = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatBoardChannels", x => new { x.Platform, x.WorkspaceId, x.BoardId });
                    table.ForeignKey(
                        name: "FK_ChatBoardChannels_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatBoardChannels_ChatWorkspaces_Platform_WorkspaceId",
                        columns: x => new { x.Platform, x.WorkspaceId },
                        principalTable: "ChatWorkspaces",
                        principalColumns: new[] { "Platform", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatAccounts_UserId",
                table: "ChatAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatBoardChannels_BoardId",
                table: "ChatBoardChannels",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatPreviewMessages_PostedAt",
                table: "ChatPreviewMessages",
                column: "PostedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatWorkspaces_AgentId",
                table: "ChatWorkspaces",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatWorkspaces_ConnectedById",
                table: "ChatWorkspaces",
                column: "ConnectedById");

            migrationBuilder.CreateIndex(
                name: "IX_SlackAppSettings_UpdatedById",
                table: "SlackAppSettings",
                column: "UpdatedById");

            // The Discord rows move over as they are; only the tables and column names change.
            migrationBuilder.Sql("""
                INSERT INTO "ChatWorkspaces" ("Platform", "WorkspaceId", "AgentId", "ConnectedById", "ConnectedAt")
                SELECT 'Discord', "GuildId", "AgentId", "ConnectedById", "ConnectedAt" FROM "DiscordGuilds";

                INSERT INTO "ChatBoardChannels" ("Platform", "WorkspaceId", "BoardId", "WholeWorkspace", "Channels")
                SELECT 'Discord', "GuildId", "BoardId", "WholeServer", "Channels" FROM "DiscordBoardChannels";

                INSERT INTO "ChatAccounts" ("Platform", "UserHash", "UserId", "LinkedAt")
                SELECT 'Discord', "DiscordUserHash", "UserId", "LinkedAt" FROM "DiscordAccounts";

                INSERT INTO "ChatPreviewMessages" ("Platform", "ChannelId", "MessageId", "WorkspaceId", "Sources", "CardIds", "BoardIds", "PostedAt")
                SELECT 'Discord', "ChannelId", "MessageId", "GuildId", "Sources", "CardIds", "BoardIds", "PostedAt" FROM "DiscordPreviewMessages";
                """);

            migrationBuilder.DropTable(
                name: "DiscordAccounts");

            migrationBuilder.DropTable(
                name: "DiscordBoardChannels");

            migrationBuilder.DropTable(
                name: "DiscordPreviewMessages");

            migrationBuilder.DropTable(
                name: "DiscordGuilds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordAccounts",
                columns: table => new
                {
                    DiscordUserHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordAccounts", x => x.DiscordUserHash);
                    table.ForeignKey(
                        name: "FK_DiscordAccounts_Principals_UserId",
                        column: x => x.UserId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscordGuilds",
                columns: table => new
                {
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConnectedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordGuilds", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_DiscordGuilds_Principals_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscordGuilds_Principals_ConnectedById",
                        column: x => x.ConnectedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DiscordPreviewMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Sources = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordPreviewMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "DiscordBoardChannels",
                columns: table => new
                {
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channels = table.Column<string>(type: "jsonb", nullable: false),
                    WholeServer = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordBoardChannels", x => new { x.GuildId, x.BoardId });
                    table.ForeignKey(
                        name: "FK_DiscordBoardChannels_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscordBoardChannels_DiscordGuilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "DiscordGuilds",
                        principalColumn: "GuildId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordAccounts_UserId",
                table: "DiscordAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordBoardChannels_BoardId",
                table: "DiscordBoardChannels",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordGuilds_AgentId",
                table: "DiscordGuilds",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordGuilds_ConnectedById",
                table: "DiscordGuilds",
                column: "ConnectedById");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordPreviewMessages_PostedAt",
                table: "DiscordPreviewMessages",
                column: "PostedAt");

            // Back to the Discord tables; Slack's rows have nowhere to go and are dropped.
            migrationBuilder.Sql("""
                INSERT INTO "DiscordGuilds" ("GuildId", "AgentId", "ConnectedById", "ConnectedAt")
                SELECT "WorkspaceId", "AgentId", "ConnectedById", "ConnectedAt" FROM "ChatWorkspaces" WHERE "Platform" = 'Discord';

                INSERT INTO "DiscordBoardChannels" ("GuildId", "BoardId", "WholeServer", "Channels")
                SELECT "WorkspaceId", "BoardId", "WholeWorkspace", "Channels" FROM "ChatBoardChannels" WHERE "Platform" = 'Discord';

                INSERT INTO "DiscordAccounts" ("DiscordUserHash", "UserId", "LinkedAt")
                SELECT "UserHash", "UserId", "LinkedAt" FROM "ChatAccounts" WHERE "Platform" = 'Discord';

                INSERT INTO "DiscordPreviewMessages" ("MessageId", "GuildId", "ChannelId", "Sources", "CardIds", "BoardIds", "PostedAt")
                SELECT "MessageId", "WorkspaceId", "ChannelId", "Sources", "CardIds", "BoardIds", "PostedAt" FROM "ChatPreviewMessages" WHERE "Platform" = 'Discord';
                """);

            migrationBuilder.DropTable(
                name: "ChatAccounts");

            migrationBuilder.DropTable(
                name: "ChatBoardChannels");

            migrationBuilder.DropTable(
                name: "ChatPreviewMessages");

            migrationBuilder.DropTable(
                name: "SlackAppSettings");

            migrationBuilder.DropTable(
                name: "ChatWorkspaces");
        }
    }
}
