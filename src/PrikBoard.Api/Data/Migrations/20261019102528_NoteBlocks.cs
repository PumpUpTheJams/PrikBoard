using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <summary>
    /// A note's body, checklist and links become blocks on the note's own canvas (one jsonb column, see NoteBlocksJson):
    /// a Text, a Checklist and a Links block, side by side, each only when the note had that content. Attached images
    /// get a place on that canvas too, in a row under the blocks.
    /// </summary>
    public partial class NoteBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Blocks",
                table: "Notes",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'");

            // Blocks keep their kind's default width (Layout.DefaultBlockSize) and sit 380 px apart; checklists and link
            // lists get a height that fits their rows, up to 400.
            migrationBuilder.Sql("""
                WITH parts AS (
                    SELECT "Id" AS note_id, 1 AS rank,
                           jsonb_build_object('kind', 'Text', 'width', 340, 'height', 220,
                                              'text', "Body", 'items', '[]'::jsonb, 'links', '[]'::jsonb) AS block
                    FROM "Notes" WHERE "Body" IS NOT NULL
                    UNION ALL
                    SELECT "Id", 2,
                           jsonb_build_object('kind', 'Checklist', 'width', 340,
                                              'height', LEAST(400, GREATEST(200, 120 + 40 * jsonb_array_length("Checklist"))),
                                              'text', NULL, 'links', '[]'::jsonb,
                                              'items', (SELECT jsonb_agg(jsonb_build_object('text', e ->> 'Text',
                                                                                            'done', COALESCE((e ->> 'Done')::boolean, false))
                                                                         ORDER BY o)
                                                        FROM jsonb_array_elements("Checklist") WITH ORDINALITY AS c(e, o)))
                    FROM "Notes" WHERE jsonb_typeof("Checklist") = 'array' AND jsonb_array_length("Checklist") > 0
                    UNION ALL
                    SELECT "Id", 3,
                           jsonb_build_object('kind', 'Links', 'width', 360,
                                              'height', LEAST(400, GREATEST(160, 120 + 40 * jsonb_array_length("Links"))),
                                              'text', NULL, 'items', '[]'::jsonb,
                                              'links', (SELECT jsonb_agg(jsonb_build_object('title', e ->> 'Title', 'url', e ->> 'Url')
                                                                         ORDER BY o)
                                                        FROM jsonb_array_elements("Links") WITH ORDINALITY AS l(e, o)))
                    FROM "Notes" WHERE jsonb_typeof("Links") = 'array' AND jsonb_array_length("Links") > 0),
                slotted AS (
                    SELECT note_id, rank, block, row_number() OVER (PARTITION BY note_id ORDER BY rank) AS slot FROM parts)
                UPDATE "Notes" n SET "Blocks" = b.blocks
                FROM (SELECT note_id,
                             jsonb_agg(block || jsonb_build_object('id', gen_random_uuid(), 'title', NULL,
                                                                   'x', 20 + 380 * (slot - 1), 'y', 20) ORDER BY slot) AS blocks
                      FROM slotted GROUP BY note_id) b
                WHERE n."Id" = b.note_id;
                """);

            // Attached images had no position: lay them out left to right, 20 px under the lowest block.
            migrationBuilder.Sql("""
                WITH placed AS (
                    SELECT i."Id",
                           20 + COALESCE(SUM(i."Width" + 20) OVER (PARTITION BY i."NoteId" ORDER BY i."CreatedAt", i."Id"
                                                                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS x,
                           20 + (SELECT COALESCE(MAX((b ->> 'y')::int + (b ->> 'height')::int), 0)
                                 FROM jsonb_array_elements(n."Blocks") AS b) AS y
                    FROM "Images" i JOIN "Notes" n ON n."Id" = i."NoteId")
                UPDATE "Images" i SET "X" = placed.x, "Y" = placed.y
                FROM placed WHERE i."Id" = placed."Id";
                """);

            migrationBuilder.DropColumn(
                name: "Body",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Checklist",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Links",
                table: "Notes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "Notes",
                type: "character varying(4000)",
                maxLength: 4000,
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

            // Loses what does not fit one body and one list each: the body is the first text block (cut to 4000
            // characters), the checklist and links are those of every block in order. Block titles and positions go.
            migrationBuilder.Sql("""
                UPDATE "Notes" n SET
                    "Body" = (SELECT left(b ->> 'text', 4000) FROM jsonb_array_elements(n."Blocks") WITH ORDINALITY AS t(b, o)
                              WHERE b ->> 'kind' = 'Text' AND b ->> 'text' IS NOT NULL ORDER BY o LIMIT 1),
                    "Checklist" = COALESCE((SELECT jsonb_agg(jsonb_build_object('Text', i ->> 'text', 'Done', (i ->> 'done')::boolean) ORDER BY o, p)
                                            FROM jsonb_array_elements(n."Blocks") WITH ORDINALITY AS t(b, o),
                                                 jsonb_array_elements(b -> 'items') WITH ORDINALITY AS c(i, p)), '[]'::jsonb),
                    "Links" = COALESCE((SELECT jsonb_agg(jsonb_build_object('Title', l ->> 'title', 'Url', l ->> 'url') ORDER BY o, p)
                                        FROM jsonb_array_elements(n."Blocks") WITH ORDINALITY AS t(b, o),
                                             jsonb_array_elements(b -> 'links') WITH ORDINALITY AS c(l, p)), '[]'::jsonb);
                """);

            migrationBuilder.DropColumn(
                name: "Blocks",
                table: "Notes");
        }
    }
}
