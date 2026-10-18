using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class NoteDetailsAndFills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AgentWorking",
                table: "Notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Assignees",
                table: "Notes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Checklist",
                table: "Notes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Links",
                table: "Notes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Tags",
                table: "Notes",
                type: "text[]",
                nullable: false,
                // Existing notes start without tags.
                defaultValueSql: "'{}'");

            // Existing notes start with empty lists rather than NULL.
            migrationBuilder.Sql("""UPDATE "Notes" SET "Assignees" = '[]', "Checklist" = '[]', "Links" = '[]';""");

            // Fills follow the board design: five named colours instead of six plain ones.
            migrationBuilder.Sql("""
                UPDATE "Notes" SET "Color" = CASE "Color"
                    WHEN 'Green' THEN 'Mint'
                    WHEN 'Blue' THEN 'Sky'
                    WHEN 'Pink' THEN 'Coral'
                    WHEN 'Orange' THEN 'Coral'
                    WHEN 'Purple' THEN 'Lilac'
                    ELSE 'Butter'
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Notes" SET "Color" = CASE "Color"
                    WHEN 'Mint' THEN 'Green'
                    WHEN 'Sky' THEN 'Blue'
                    WHEN 'Coral' THEN 'Pink'
                    WHEN 'Lilac' THEN 'Purple'
                    ELSE 'Yellow'
                END;
                """);

            migrationBuilder.DropColumn(
                name: "AgentWorking",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Assignees",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Checklist",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Links",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Notes");
        }
    }
}
