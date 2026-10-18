using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Principals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MaxRole = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessTokens_Principals_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessTokens_Principals_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Principals_OwnerId",
                table: "Principals",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_AgentId",
                table: "AccessTokens",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_OwnerId",
                table: "AccessTokens",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_TokenHash",
                table: "AccessTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Principals_Principals_OwnerId",
                table: "Principals",
                column: "OwnerId",
                principalTable: "Principals",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Principals_Principals_OwnerId",
                table: "Principals");

            migrationBuilder.DropTable(
                name: "AccessTokens");

            migrationBuilder.DropIndex(
                name: "IX_Principals_OwnerId",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Principals");
        }
    }
}
