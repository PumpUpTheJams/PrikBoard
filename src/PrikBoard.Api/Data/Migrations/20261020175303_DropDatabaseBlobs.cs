using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropDatabaseBlobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // StartupMigration moves the bytes to disk first (it runs this migration only afterwards). Anything that runs the
            // migrations without it, such as dotnet ef database update, stops here instead of dropping images and avatars.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Images" WHERE "FileId" IS NULL)
                       OR EXISTS (SELECT 1 FROM "UserAvatars" a JOIN "Principals" p ON p."Id" = a."UserId" WHERE p."AvatarFileId" IS NULL) THEN
                        RAISE EXCEPTION 'Images or avatars are still in the database. Start the API, which moves them to disk before this migration.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "ImageContents");

            migrationBuilder.DropTable(
                name: "UserAvatars");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "Images");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileId",
                table: "Images",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "The image and avatar bytes now live on disk (Storage:Path). To go back, restore the database dump taken before upgrading.");
        }
    }
}
