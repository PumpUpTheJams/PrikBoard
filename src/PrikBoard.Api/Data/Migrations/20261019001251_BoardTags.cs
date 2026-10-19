using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BoardTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BoardTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardTags_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardTags_BoardId_Name",
                table: "BoardTags",
                columns: new[] { "BoardId", "Name" },
                unique: true);

            // Every tag already on a note becomes one of its board's tags (they are normalized and at most 30 characters).
            migrationBuilder.Sql("""
                INSERT INTO "BoardTags" ("Id", "BoardId", "Name", "CreatedAt")
                SELECT gen_random_uuid(), n."BoardId", t.name, now()
                FROM "Notes" n CROSS JOIN LATERAL unnest(n."Tags") AS t(name)
                WHERE t.name <> ''
                GROUP BY n."BoardId", t.name;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardTags");
        }
    }
}
