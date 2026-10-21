using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscordIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordConnectGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChannelBoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    WholeServerBoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "DiscordGuilds",
                columns: table => new
                {
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "DiscordBoardChannels",
                columns: table => new
                {
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channels = table.Column<string>(type: "jsonb", nullable: true)
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
                name: "IX_DiscordBoardChannels_BoardId",
                table: "DiscordBoardChannels",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordConnectGrants_CodeHash",
                table: "DiscordConnectGrants",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordConnectGrants_OwnerId",
                table: "DiscordConnectGrants",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordGuilds_AgentId",
                table: "DiscordGuilds",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordGuilds_ConnectedById",
                table: "DiscordGuilds",
                column: "ConnectedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordBoardChannels");

            migrationBuilder.DropTable(
                name: "DiscordConnectGrants");

            migrationBuilder.DropTable(
                name: "DiscordGuilds");
        }
    }
}
