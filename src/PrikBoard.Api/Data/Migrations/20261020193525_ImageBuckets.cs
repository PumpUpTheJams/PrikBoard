using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImageBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BucketId",
                table: "Images",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Images_BucketId",
                table: "Images",
                column: "BucketId");

            migrationBuilder.AddForeignKey(
                name: "FK_Images_Buckets_BucketId",
                table: "Images",
                column: "BucketId",
                principalTable: "Buckets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Images on the board join the bucket they sit in now: the topmost live one containing their centre (Layout.Place).
            migrationBuilder.Sql("""
                UPDATE "Images" AS i SET "BucketId" = (
                    SELECT b."Id" FROM "Buckets" AS b
                    WHERE b."BoardId" = i."BoardId" AND b."DeletedAt" IS NULL
                      AND i."X" + i."Width" / 2 >= b."X" AND i."X" + i."Width" / 2 < b."X" + b."Width"
                      AND i."Y" + i."Height" / 2 >= b."Y" AND i."Y" + i."Height" / 2 < b."Y" + b."Height"
                    ORDER BY b."CreatedAt" DESC, b."Id" DESC
                    LIMIT 1)
                WHERE i."NoteId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Images_Buckets_BucketId",
                table: "Images");

            migrationBuilder.DropIndex(
                name: "IX_Images_BucketId",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "BucketId",
                table: "Images");
        }
    }
}
