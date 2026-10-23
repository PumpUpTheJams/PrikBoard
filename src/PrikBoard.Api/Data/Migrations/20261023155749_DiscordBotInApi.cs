using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscordBotInApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The bot runs in the API now, so connected servers need no token of their own. Their agents and board
            // memberships stay: entering the same bot's token on the admin page brings their previews back.
            migrationBuilder.Sql("""DELETE FROM "AccessTokens" WHERE "Kind" = 'Discord';""");

            migrationBuilder.DropTable(
                name: "DiscordConnectGrants");

            migrationBuilder.CreateTable(
                name: "DiscordBotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ProtectedBotToken = table.Column<string>(type: "text", nullable: true),
                    ApplicationId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BotName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PublicUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordBotSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscordBotSettings_Principals_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordBotSettings_UpdatedById",
                table: "DiscordBotSettings",
                column: "UpdatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordBotSettings");

            migrationBuilder.CreateTable(
                name: "DiscordConnectGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelBoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WholeServerBoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordConnectGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscordConnectGrants_Principals_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordConnectGrants_CodeHash",
                table: "DiscordConnectGrants",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordConnectGrants_OwnerId",
                table: "DiscordConnectGrants",
                column: "OwnerId");
        }
    }
}
