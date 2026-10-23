using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscordActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WholeServer",
                table: "DiscordBoardChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The whole server was a null list of channels; now it is a flag next to the channels, which may both be set.
            migrationBuilder.Sql("""
                UPDATE "DiscordBoardChannels" SET "WholeServer" = TRUE, "Channels" = '[]'::jsonb WHERE "Channels" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Channels",
                table: "DiscordBoardChannels",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

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
                name: "DiscordPreviewMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Sources = table.Column<List<string>>(type: "text[]", nullable: false),
                    CardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    BoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordPreviewMessages", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardChanges_At",
                table: "CardChanges",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordAccounts_UserId",
                table: "DiscordAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordPreviewMessages_PostedAt",
                table: "DiscordPreviewMessages",
                column: "PostedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordAccounts");

            migrationBuilder.DropTable(
                name: "DiscordPreviewMessages");

            migrationBuilder.DropIndex(
                name: "IX_CardChanges_At",
                table: "CardChanges");

            migrationBuilder.AlterColumn<string>(
                name: "Channels",
                table: "DiscordBoardChannels",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.Sql("""
                UPDATE "DiscordBoardChannels" SET "Channels" = NULL WHERE "WholeServer";
                """);

            migrationBuilder.DropColumn(
                name: "WholeServer",
                table: "DiscordBoardChannels");
        }
    }
}
