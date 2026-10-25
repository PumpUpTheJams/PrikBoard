using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SlackApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_SlackAppSettings_UpdatedById",
                table: "SlackAppSettings",
                column: "UpdatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlackAppSettings");
        }
    }
}
