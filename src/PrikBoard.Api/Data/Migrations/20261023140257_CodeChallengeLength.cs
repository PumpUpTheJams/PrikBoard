using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeChallengeLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CodeChallenge",
                table: "DiscordConnectGrants",
                type: "character varying(43)",
                maxLength: 43,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "CodeChallenge",
                table: "AppSignInGrants",
                type: "character varying(43)",
                maxLength: 43,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CodeChallenge",
                table: "DiscordConnectGrants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(43)",
                oldMaxLength: 43);

            migrationBuilder.AlterColumn<string>(
                name: "CodeChallenge",
                table: "AppSignInGrants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(43)",
                oldMaxLength: 43);
        }
    }
}
