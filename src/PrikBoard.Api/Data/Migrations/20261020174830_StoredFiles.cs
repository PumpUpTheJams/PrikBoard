using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoredFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AvatarFileId",
                table: "Principals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FileId",
                table: "Images",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Principals_AvatarFileId",
                table: "Principals",
                column: "AvatarFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Images_FileId",
                table: "Images",
                column: "FileId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Images_StoredFiles_FileId",
                table: "Images",
                column: "FileId",
                principalTable: "StoredFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Principals_StoredFiles_AvatarFileId",
                table: "Principals",
                column: "AvatarFileId",
                principalTable: "StoredFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Images_StoredFiles_FileId",
                table: "Images");

            migrationBuilder.DropForeignKey(
                name: "FK_Principals_StoredFiles_AvatarFileId",
                table: "Principals");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropIndex(
                name: "IX_Principals_AvatarFileId",
                table: "Principals");

            migrationBuilder.DropIndex(
                name: "IX_Images_FileId",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "AvatarFileId",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "FileId",
                table: "Images");
        }
    }
}
