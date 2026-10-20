using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoredFileBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BoardId",
                table: "StoredFiles",
                type: "uuid",
                nullable: true);

            // An image's file lives in its board's folder (FileStore.PathOf); avatars have no board. This also covers the
            // files StartupMigration moved out of the database just before, which it already put in their board's folder.
            migrationBuilder.Sql("""
                UPDATE "StoredFiles" f SET "BoardId" = i."BoardId" FROM "Images" i WHERE i."FileId" = f."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "StoredFiles");
        }
    }
}
