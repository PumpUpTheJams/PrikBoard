using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppSignIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AgentId",
                table: "AccessTokens",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "AccessTokens",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                // Every existing token acts as its own agent.
                defaultValue: "Agent");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "AccessTokens",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppSignInGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSignInGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppSignInGrants_Principals_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppSignInGrants_CodeHash",
                table: "AppSignInGrants",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppSignInGrants_OwnerId",
                table: "AppSignInGrants",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // App tokens have no agent, which the old schema requires.
            migrationBuilder.Sql("""DELETE FROM "AccessTokens" WHERE "AgentId" IS NULL;""");

            migrationBuilder.DropTable(
                name: "AppSignInGrants");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AccessTokens");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "AccessTokens");

            migrationBuilder.AlterColumn<Guid>(
                name: "AgentId",
                table: "AccessTokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
