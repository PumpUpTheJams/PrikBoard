using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadableIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "Notes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "Boards",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NextNoteNumber",
                table: "Boards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ShortId",
                table: "Boards",
                type: "character varying(6)",
                maxLength: 6,
                nullable: false,
                defaultValue: "");

            // A random short id per board (Board.NewShortId), drawn again on a clash, and a key from its name like
            // Board.KeyFrom: the initials of its words, or the start of the name when that gives fewer than two, at most
            // four, never starting with a digit, "PB" when nothing is left.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    alphabet constant text := '0123456789abcdefghjkmnpqrstvwxyz';
                    board record;
                    candidate text;
                    words text[];
                    word text;
                    initials text;
                    board_key text;
                BEGIN
                    FOR board IN SELECT "Id", "Name" FROM "Boards" LOOP
                        LOOP
                            candidate := '';
                            FOR i IN 1..6 LOOP
                                candidate := candidate || substr(alphabet, 1 + floor(random() * 32)::int, 1);
                            END LOOP;
                            EXIT WHEN NOT EXISTS (SELECT 1 FROM "Boards" WHERE "ShortId" = candidate);
                        END LOOP;

                        words := array_remove(regexp_split_to_array(upper(board."Name"), '[^A-Z0-9]+'), '');
                        initials := '';
                        FOREACH word IN ARRAY words LOOP
                            initials := initials || left(word, 1);
                        END LOOP;
                        initials := ltrim(initials, '0123456789');
                        board_key := left(CASE WHEN length(initials) >= 2 THEN initials
                                               ELSE ltrim(array_to_string(words, ''), '0123456789') END, 4);
                        IF length(board_key) < 2 THEN board_key := 'PB'; END IF;

                        UPDATE "Boards" SET "ShortId" = candidate, "Key" = board_key WHERE "Id" = board."Id";
                    END LOOP;
                END $$;
                """);

            // Notes are numbered per board in the order they were created, deleted ones too: they keep their number.
            migrationBuilder.Sql("""
                UPDATE "Notes" AS n SET "Number" = numbered."Number"
                FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "BoardId" ORDER BY "CreatedAt", "Id") AS "Number" FROM "Notes") AS numbered
                WHERE numbered."Id" = n."Id";

                UPDATE "Boards" AS b SET "NextNoteNumber" = 1 + COALESCE((SELECT MAX(n."Number") FROM "Notes" AS n WHERE n."BoardId" = b."Id"), 0);
                """);

            // Links to notes in the older form, /#/boards/{boardId}/notes/{noteId}, become the note's path (NoteLinks.NotePath)
            // when that note is still there, deleted or not; other links stay as they are. Blocks are camelCase jsonb
            // (NoteBlocksJson).
            migrationBuilder.Sql("""
                UPDATE "Notes" AS n SET "Blocks" = (
                    SELECT jsonb_agg(
                        CASE WHEN block->>'kind' = 'Links' THEN jsonb_set(block, '{links}', COALESCE((
                            SELECT jsonb_agg(
                                CASE WHEN target."Id" IS NULL THEN link
                                     ELSE jsonb_set(link, '{url}', to_jsonb('/#/boards/' || board."ShortId" || '/notes/' || target."Number"))
                                END ORDER BY link_index)
                            FROM jsonb_array_elements(block->'links') WITH ORDINALITY AS l(link, link_index)
                            LEFT JOIN "Notes" AS target ON target."Id"::text = lower(substring(link->>'url'
                                FROM '^/#/boards/[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}/notes/([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$'))
                            LEFT JOIN "Boards" AS board ON board."Id" = target."BoardId"), '[]'::jsonb))
                        ELSE block END
                        ORDER BY block_index)
                    FROM jsonb_array_elements(n."Blocks") WITH ORDINALITY AS b(block, block_index))
                WHERE n."Blocks"::text LIKE '%/notes/%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Notes_BoardId_Number",
                table: "Notes",
                columns: new[] { "BoardId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Boards_ShortId",
                table: "Boards",
                column: "ShortId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Links back in the older form, which is all that the code before this migration reads.
            migrationBuilder.Sql("""
                UPDATE "Notes" AS n SET "Blocks" = (
                    SELECT jsonb_agg(
                        CASE WHEN block->>'kind' = 'Links' THEN jsonb_set(block, '{links}', COALESCE((
                            SELECT jsonb_agg(
                                CASE WHEN target."Id" IS NULL THEN link
                                     ELSE jsonb_set(link, '{url}', to_jsonb('/#/boards/' || target."BoardId" || '/notes/' || target."Id"))
                                END ORDER BY link_index)
                            FROM jsonb_array_elements(block->'links') WITH ORDINALITY AS l(link, link_index)
                            LEFT JOIN "Boards" AS board ON board."ShortId" = substring(link->>'url' FROM '^/#/boards/([0-9a-z]{6})/notes/[1-9][0-9]*$')
                            LEFT JOIN "Notes" AS target ON target."BoardId" = board."Id"
                                AND target."Number"::text = substring(link->>'url' FROM '^/#/boards/[0-9a-z]{6}/notes/([1-9][0-9]*)$')), '[]'::jsonb))
                        ELSE block END
                        ORDER BY block_index)
                    FROM jsonb_array_elements(n."Blocks") WITH ORDINALITY AS b(block, block_index))
                WHERE n."Blocks"::text LIKE '%/notes/%';
                """);

            migrationBuilder.DropIndex(
                name: "IX_Notes_BoardId_Number",
                table: "Notes");

            migrationBuilder.DropIndex(
                name: "IX_Boards_ShortId",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "NextNoteNumber",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "ShortId",
                table: "Boards");
        }
    }
}
