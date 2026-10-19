using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BoardTags_BoardId_Name",
                table: "BoardTags");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Images",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Buckets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "BoardTags",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "RemovedFromNoteIds",
                table: "BoardTags",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardTags_BoardId_Name",
                table: "BoardTags",
                columns: new[] { "BoardId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BoardTags_BoardId_Name",
                table: "BoardTags");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Buckets");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "BoardTags");

            migrationBuilder.DropColumn(
                name: "RemovedFromNoteIds",
                table: "BoardTags");

            migrationBuilder.CreateIndex(
                name: "IX_BoardTags_BoardId_Name",
                table: "BoardTags",
                columns: new[] { "BoardId", "Name" },
                unique: true);
        }
    }
}
