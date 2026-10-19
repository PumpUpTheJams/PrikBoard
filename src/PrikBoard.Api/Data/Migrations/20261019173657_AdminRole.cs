using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Principals",
                type: "boolean",
                nullable: true);

            // Users need a value (agents keep null); the oldest user runs the instance from now on.
            migrationBuilder.Sql("""
                UPDATE "Principals" SET "IsAdmin" = false WHERE "Kind" = 'User';
                UPDATE "Principals" SET "IsAdmin" = true
                WHERE "Id" = (SELECT "Id" FROM "Principals" WHERE "Kind" = 'User' ORDER BY "CreatedAt", "Id" LIMIT 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Principals");
        }
    }
}
