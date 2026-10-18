using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceColumnsWithBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Buckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    X = table.Column<int>(type: "INTEGER", nullable: false),
                    Y = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buckets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Buckets_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Each column becomes a 420 px wide bucket, side by side in column order, tall enough for two notes per row.
            // Buckets keep the column ids so notes can find theirs. CreatedAt sets the stacking order: the board's own
            // timestamp plus one millisecond per position (one millisecond is 20480 in the binary DateTimeOffset format).
            migrationBuilder.Sql("""
                INSERT INTO "Buckets" ("Id", "BoardId", "Name", "Color", "X", "Y", "Width", "Height", "CreatedAt")
                SELECT c."Id", c."BoardId", c."Name",
                       CASE c."Position" % 5 WHEN 0 THEN 'Blue' WHEN 1 THEN 'Amber' WHEN 2 THEN 'Teal' WHEN 3 THEN 'Violet' ELSE 'Rose' END,
                       40 + c."Position" * 460, 40, 420,
                       MAX(600, 40 + ((SELECT COUNT(*) FROM "Notes" n WHERE n."ColumnId" = c."Id") + 1) / 2 * 160),
                       b."CreatedAt" + c."Position" * 20480
                FROM "Columns" c JOIN "Boards" b ON b."Id" = c."BoardId";
                """);

            // Rebuild Notes by hand rather than with EF's table-rebuild operations: EF runs those last, after the
            // Columns table is dropped, and dropping Columns while Notes still references it cascades to every note.
            // Notes fill their bucket two per row in their old column order.
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Notes" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Notes" PRIMARY KEY,
                    "BoardId" TEXT NOT NULL,
                    "Body" TEXT NULL,
                    "BucketId" TEXT NULL,
                    "Color" TEXT NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "Title" TEXT NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL,
                    "X" INTEGER NOT NULL,
                    "Y" INTEGER NOT NULL,
                    CONSTRAINT "FK_Notes_Boards_BoardId" FOREIGN KEY ("BoardId") REFERENCES "Boards" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_Notes_Buckets_BucketId" FOREIGN KEY ("BucketId") REFERENCES "Buckets" ("Id") ON DELETE SET NULL
                );

                INSERT INTO "ef_temp_Notes" ("Id", "BoardId", "Body", "BucketId", "Color", "CreatedAt", "Title", "UpdatedAt", "X", "Y")
                SELECT n."Id", b."BoardId", n."Body", b."Id", n."Color", n."CreatedAt", n."Title", n."UpdatedAt",
                       b."X" + 20 + (n."Position" % 2) * 180,
                       b."Y" + 40 + (n."Position" / 2) * 160
                FROM "Notes" n JOIN "Buckets" b ON b."Id" = n."ColumnId";

                DROP TABLE "Notes";
                ALTER TABLE "ef_temp_Notes" RENAME TO "Notes";
                DROP TABLE "Columns";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Notes_BoardId",
                table: "Notes",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_BucketId",
                table: "Notes",
                column: "BucketId");

            migrationBuilder.CreateIndex(
                name: "IX_Buckets_BoardId",
                table: "Buckets",
                column: "BoardId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Freeform positions and loose notes have no column equivalent.
            throw new NotSupportedException("Columns cannot be restored from buckets. Restore a database backup instead.");
        }
    }
}
