using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <summary>
    /// A drawing becomes one or more strokes: its single stroke's points move into a jsonb list of strokes (with the stroke
    /// width), and its color into a list of inks, one per stroke. Revision versions the content's URL.
    /// </summary>
    public partial class DrawingStrokes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Colors",
                table: "Drawings",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Drawings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Strokes",
                table: "DrawingContents",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Names as DrawingStrokesJson writes them (camelCase).
            migrationBuilder.Sql("""
                UPDATE "Drawings" SET "Colors" = ARRAY["Color"]::text[];
                UPDATE "DrawingContents" c
                SET "Strokes" = jsonb_build_array(jsonb_build_object('strokeWidth', d."StrokeWidth", 'points', c."Points"))
                FROM "Drawings" d
                WHERE d."Id" = c."DrawingId";
                """);

            migrationBuilder.DropColumn(name: "Color", table: "Drawings");
            migrationBuilder.DropColumn(name: "StrokeWidth", table: "Drawings");
            migrationBuilder.DropColumn(name: "Points", table: "DrawingContents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Drawings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Chalk");

            migrationBuilder.AddColumn<int>(
                name: "StrokeWidth",
                table: "Drawings",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<string>(
                name: "Points",
                table: "DrawingContents",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Only the first stroke of each drawing fits the old shape.
            migrationBuilder.Sql("""
                UPDATE "Drawings" SET "Color" = COALESCE("Colors"[1], 'Chalk');
                UPDATE "Drawings" d
                SET "StrokeWidth" = COALESCE((c."Strokes" -> 0 ->> 'strokeWidth')::integer, 4)
                FROM "DrawingContents" c
                WHERE d."Id" = c."DrawingId";
                UPDATE "DrawingContents" SET "Points" = COALESCE("Strokes" -> 0 -> 'points', '[]'::jsonb);
                """);

            migrationBuilder.DropColumn(name: "Colors", table: "Drawings");
            migrationBuilder.DropColumn(name: "Revision", table: "Drawings");
            migrationBuilder.DropColumn(name: "Strokes", table: "DrawingContents");
        }
    }
}
