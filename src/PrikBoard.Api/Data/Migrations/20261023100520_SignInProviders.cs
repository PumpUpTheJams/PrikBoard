using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignInProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignInProviders",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Authority = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    EmailsAreVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    GitHubAppUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInProviders", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInProviders");
        }
    }
}
