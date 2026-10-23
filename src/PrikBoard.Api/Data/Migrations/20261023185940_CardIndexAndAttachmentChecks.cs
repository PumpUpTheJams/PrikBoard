using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardIndexAndAttachmentChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cards_BoardId",
                table: "Cards");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Images_CardOrBucket",
                table: "Images",
                sql: "\"CardId\" IS NULL OR \"BucketId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Drawings_CardOrBucket",
                table: "Drawings",
                sql: "\"CardId\" IS NULL OR \"BucketId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Images_CardOrBucket",
                table: "Images");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Drawings_CardOrBucket",
                table: "Drawings");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_BoardId",
                table: "Cards",
                column: "BoardId");
        }
    }
}
