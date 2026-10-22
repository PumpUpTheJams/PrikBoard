using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LastBoardId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastBoardId",
                table: "Principals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Principals_LastBoardId",
                table: "Principals",
                column: "LastBoardId");

            migrationBuilder.AddForeignKey(
                name: "FK_Principals_Boards_LastBoardId",
                table: "Principals",
                column: "LastBoardId",
                principalTable: "Boards",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Principals_Boards_LastBoardId",
                table: "Principals");

            migrationBuilder.DropIndex(
                name: "IX_Principals_LastBoardId",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "LastBoardId",
                table: "Principals");
        }
    }
}
