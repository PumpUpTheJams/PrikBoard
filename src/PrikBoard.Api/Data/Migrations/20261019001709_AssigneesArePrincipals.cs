using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <summary>
    /// Assignees become principal ids instead of free-text names. Each old name is linked to the principal on the note's
    /// board with that shown name (display name, or the fallback such as "User 1a2b"), case-insensitively; names that
    /// match no one are dropped.
    /// </summary>
    public partial class AssigneesArePrincipals : Migration
    {
        /// <summary>
        /// Shown names (as Principal.ShownName) of who can be assigned on each board: members, and the agents of members'
        /// access tokens that reach the board (as BoardAccess.AssignableIds). "via" ranks members first.
        /// </summary>
        private const string Assignable = """
            shown AS (
                SELECT p."Id", p."Kind",
                       COALESCE(p."DisplayName",
                                CASE p."Kind" WHEN 'User' THEN 'User ' || right(replace(p."Id"::text, '-', ''), 4)
                                              ELSE 'Agent ' || left(p."ClientId", 60) END) AS name
                FROM "Principals" p),
            assignable AS (
                SELECT m."BoardId", s."Id", s."Kind", s.name, 0 AS via
                FROM "BoardMembers" m JOIN shown s ON s."Id" = m."PrincipalId"
                UNION ALL
                SELECT m."BoardId", s."Id", s."Kind", s.name, 1 AS via
                FROM "BoardMembers" m
                JOIN "AccessTokens" t ON t."OwnerId" = m."PrincipalId" AND t."ExpiresAt" > now()
                                     AND (t."BoardIds" IS NULL OR m."BoardId" = ANY (t."BoardIds"))
                JOIN shown s ON s."Id" = t."AgentId")
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "AssigneeIds",
                table: "Notes",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // set_working stored names cut to 60 characters, so both sides are compared that way. With several matches the
            // same kind wins, then members over token agents. The first occurrence keeps its place in the list.
            migrationBuilder.Sql($"""
                WITH {Assignable},
                entries AS (
                    SELECT n."Id" AS note_id, n."BoardId", e.ord,
                           lower(left(btrim(e.value ->> 'Name'), 60)) AS name, e.value ->> 'Kind' AS kind
                    FROM "Notes" n
                    CROSS JOIN LATERAL jsonb_array_elements(COALESCE(n."Assignees", '[]'::jsonb)) WITH ORDINALITY AS e(value, ord)),
                matched AS (
                    SELECT DISTINCT ON (en.note_id, en.ord) en.note_id, en.ord, a."Id" AS principal_id
                    FROM entries en
                    JOIN assignable a ON a."BoardId" = en."BoardId" AND lower(left(a.name, 60)) = en.name
                    ORDER BY en.note_id, en.ord,
                             (a."Kind" = CASE en.kind WHEN 'Agent' THEN 'Agent' ELSE 'User' END) DESC, a.via, a."Id"),
                firsts AS (
                    SELECT note_id, principal_id, min(ord) AS ord FROM matched GROUP BY note_id, principal_id)
                UPDATE "Notes" n SET "AssigneeIds" = x.ids
                FROM (SELECT note_id, array_agg(principal_id ORDER BY ord) AS ids FROM firsts GROUP BY note_id) x
                WHERE n."Id" = x.note_id;
                """);

            migrationBuilder.DropColumn(
                name: "Assignees",
                table: "Notes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Assignees",
                table: "Notes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("""
                WITH shown AS (
                    SELECT p."Id", p."Kind",
                           COALESCE(p."DisplayName",
                                    CASE p."Kind" WHEN 'User' THEN 'User ' || right(replace(p."Id"::text, '-', ''), 4)
                                                  ELSE 'Agent ' || left(p."ClientId", 60) END) AS name
                    FROM "Principals" p)
                UPDATE "Notes" n SET "Assignees" = COALESCE((
                    SELECT jsonb_agg(jsonb_build_object(
                               'Name', left(COALESCE(s.name, 'Unknown'), 60),
                               'Kind', CASE s."Kind" WHEN 'Agent' THEN 'Agent' ELSE 'Person' END) ORDER BY a.ord)
                    FROM unnest(n."AssigneeIds") WITH ORDINALITY AS a(id, ord)
                    LEFT JOIN shown s ON s."Id" = a.id), '[]'::jsonb);
                """);

            migrationBuilder.DropColumn(
                name: "AssigneeIds",
                table: "Notes");
        }
    }
}
