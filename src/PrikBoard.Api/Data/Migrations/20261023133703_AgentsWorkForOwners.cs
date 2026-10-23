using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentsWorkForOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentWorkingForId",
                table: "Cards",
                type: "uuid",
                nullable: true);

            // A card being worked on names who for: the owner of a token agent among its assignees, or else an agent of
            // its own (an issuer client). Then agents behind a person's token stop being assignees: their owner takes
            // their place, once, where the first of them was. Deleted cards too (raw SQL skips the query filter).
            migrationBuilder.Sql("""
                UPDATE "Cards" c
                SET "AgentWorkingForId" = (
                    SELECT COALESCE(p."OwnerId", p."Id")
                    FROM unnest(c."AssigneeIds") WITH ORDINALITY AS a(id, n)
                    JOIN "Principals" p ON p."Id" = a.id AND p."Kind" = 'Agent' AND p."Issuer" <> 'discord'
                    ORDER BY p."OwnerId" IS NULL, a.n
                    LIMIT 1)
                WHERE c."AgentWorking";

                UPDATE "Cards" c
                SET "AssigneeIds" = ARRAY(
                    SELECT s.id FROM (
                        SELECT COALESCE(p."OwnerId", a.id) AS id, MIN(a.n) AS n
                        FROM unnest(c."AssigneeIds") WITH ORDINALITY AS a(id, n)
                        LEFT JOIN "Principals" p ON p."Id" = a.id AND p."OwnerId" IS NOT NULL
                        GROUP BY 1) s
                    ORDER BY s.n)
                WHERE EXISTS (
                    SELECT 1 FROM unnest(c."AssigneeIds") AS x(id)
                    JOIN "Principals" p ON p."Id" = x.id AND p."OwnerId" IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The owners stay assigned: which agent an owner stood in for isn't kept.
            migrationBuilder.DropColumn(
                name: "AgentWorkingForId",
                table: "Cards");
        }
    }
}
