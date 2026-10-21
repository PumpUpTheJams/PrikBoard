using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <summary>
    /// Notes become cards and their blocks components: the table, its columns, keys and indexes and the columns that point at
    /// it are renamed in place, so no data moves. Links to cards in the web app's address form
    /// (/#/boards/{board}/notes/{card}) become /#/boards/{board}/cards/{card} wherever they are stored as text: in components
    /// (link urls and Markdown) and in a board's description and rules.
    /// </summary>
    public partial class CardsAndComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Rename(migrationBuilder, from: ("Note", "Block"), to: ("Card", "Component"));
            RewriteLinks(migrationBuilder, from: "notes", to: "cards");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RewriteLinks(migrationBuilder, from: "cards", to: "notes");
            Rename(migrationBuilder, from: ("Card", "Component"), to: ("Note", "Block"));
        }

        private static void Rename(MigrationBuilder migrationBuilder, (string Item, string Part) from, (string Item, string Part) to)
        {
            var (oldTable, newTable) = (from.Item + "s", to.Item + "s");
            var (oldId, newId) = (from.Item + "Id", to.Item + "Id");

            migrationBuilder.RenameTable(name: oldTable, newName: newTable);
            migrationBuilder.RenameColumn(name: from.Part + "s", table: newTable, newName: to.Part + "s");
            migrationBuilder.Sql($"""
                ALTER TABLE "{newTable}" RENAME CONSTRAINT "PK_{oldTable}" TO "PK_{newTable}";
                ALTER TABLE "{newTable}" RENAME CONSTRAINT "FK_{oldTable}_Boards_BoardId" TO "FK_{newTable}_Boards_BoardId";
                ALTER TABLE "{newTable}" RENAME CONSTRAINT "FK_{oldTable}_Buckets_BucketId" TO "FK_{newTable}_Buckets_BucketId";
                ALTER TABLE "{newTable}" RENAME CONSTRAINT "FK_{oldTable}_Principals_CreatedById" TO "FK_{newTable}_Principals_CreatedById";
                """);
            foreach (var index in new[] { "BoardId", "BoardId_Number", "BucketId", "CreatedById" })
                migrationBuilder.RenameIndex(name: $"IX_{oldTable}_{index}", table: newTable, newName: $"IX_{newTable}_{index}");

            foreach (var table in new[] { "Images", "Drawings", "ImageUploads" })
            {
                migrationBuilder.RenameColumn(name: oldId, table: table, newName: newId);
                migrationBuilder.RenameIndex(name: $"IX_{table}_{oldId}", table: table, newName: $"IX_{table}_{newId}");
                migrationBuilder.Sql($"""
                    ALTER TABLE "{table}" RENAME CONSTRAINT "FK_{table}_{oldTable}_{oldId}" TO "FK_{table}_{newTable}_{newId}";
                    """);
            }

            migrationBuilder.RenameColumn(name: $"RemovedFrom{from.Item}Ids", table: "BoardTags", newName: $"RemovedFrom{to.Item}Ids");
            migrationBuilder.RenameColumn(name: $"Next{from.Item}Number", table: "Boards", newName: $"Next{to.Item}Number");
        }

        /// <summary>Both address forms: the board's short id or id, then a card's number or id. Runs on the Cards table.</summary>
        private static void RewriteLinks(MigrationBuilder migrationBuilder, string from, string to)
        {
            var pattern = $"(/#/boards/[0-9A-Za-z-]+)/{from}/";
            migrationBuilder.Sql($"""
                UPDATE "Cards" SET "Components" = regexp_replace("Components"::text, '{pattern}', '\1/{to}/', 'g')::jsonb
                WHERE "Components"::text ~ '{pattern}';

                UPDATE "Boards" SET
                    "Description" = regexp_replace("Description", '{pattern}', '\1/{to}/', 'g'),
                    "Rules" = regexp_replace("Rules", '{pattern}', '\1/{to}/', 'g')
                WHERE "Description" ~ '{pattern}' OR "Rules" ~ '{pattern}';
                """);
        }
    }
}
