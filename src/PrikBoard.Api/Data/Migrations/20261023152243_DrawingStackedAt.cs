using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DrawingStackedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StackedAt",
                table: "Drawings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Loose drawings were drawn above the buckets until now (all but the front one), so they start on top, the
            // oldest lowest.
            migrationBuilder.Sql("""
                UPDATE "Drawings" d SET "StackedAt" = now() + o.n * interval '1 millisecond'
                FROM (SELECT "Id", row_number() OVER (PARTITION BY "BoardId" ORDER BY "CreatedAt", "Id") AS n FROM "Drawings") o
                WHERE d."Id" = o."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StackedAt",
                table: "Drawings");
        }
    }
}
