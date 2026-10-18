using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsersAndMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Users no longer store names, emails or provider ids, and boards now need owners. Rather than migrate
            // personal data into the new shape, start fresh: this clears all boards (with their buckets, notes and
            // images, by cascade) and all users. Only development data existed when this was written.
            migrationBuilder.Sql("""DELETE FROM "Boards";""");
            migrationBuilder.Sql("""DELETE FROM "Users";""");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Users",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Provider_Subject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AddedBy",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "Users");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "Principals");

            migrationBuilder.RenameColumn(
                name: "LastLoginAt",
                table: "Principals",
                newName: "LastSeenAt");

            migrationBuilder.AddColumn<Guid>(
                name: "AddedById",
                table: "Images",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "Principals",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Principals",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Issuer",
                table: "Principals",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Principals",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Principals",
                table: "Principals",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "BoardInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardInvites_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BoardInvites_Principals_AcceptedById",
                        column: x => x.AcceptedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BoardInvites_Principals_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BoardMembers",
                columns: table => new
                {
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardMembers", x => new { x.BoardId, x.PrincipalId });
                    table.ForeignKey(
                        name: "FK_BoardMembers_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BoardMembers_Principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SubjectHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    EmailHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserIdentities_Principals_UserId",
                        column: x => x.UserId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Images_AddedById",
                table: "Images",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_Principals_Issuer_ClientId",
                table: "Principals",
                columns: new[] { "Issuer", "ClientId" },
                unique: true,
                filter: "\"Kind\" = 'Agent'");

            migrationBuilder.CreateIndex(
                name: "IX_BoardInvites_AcceptedById",
                table: "BoardInvites",
                column: "AcceptedById");

            migrationBuilder.CreateIndex(
                name: "IX_BoardInvites_BoardId",
                table: "BoardInvites",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardInvites_CreatedById",
                table: "BoardInvites",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_BoardInvites_TokenHash",
                table: "BoardInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardMembers_PrincipalId",
                table: "BoardMembers",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_EmailHash",
                table: "UserIdentities",
                column: "EmailHash");

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_Provider_SubjectHash",
                table: "UserIdentities",
                columns: new[] { "Provider", "SubjectHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_UserId",
                table: "UserIdentities",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Images_Principals_AddedById",
                table: "Images",
                column: "AddedById",
                principalTable: "Principals",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Images_Principals_AddedById",
                table: "Images");

            migrationBuilder.DropTable(
                name: "BoardInvites");

            migrationBuilder.DropTable(
                name: "BoardMembers");

            migrationBuilder.DropTable(
                name: "UserIdentities");

            migrationBuilder.DropIndex(
                name: "IX_Images_AddedById",
                table: "Images");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Principals",
                table: "Principals");

            migrationBuilder.DropIndex(
                name: "IX_Principals_Issuer_ClientId",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "AddedById",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "Issuer",
                table: "Principals");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Principals");

            migrationBuilder.RenameTable(
                name: "Principals",
                newName: "Users");

            migrationBuilder.RenameColumn(
                name: "LastSeenAt",
                table: "Users",
                newName: "LastLoginAt");

            migrationBuilder.AddColumn<string>(
                name: "AddedBy",
                table: "Images",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Users",
                table: "Users",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Provider_Subject",
                table: "Users",
                columns: new[] { "Provider", "Subject" },
                unique: true);
        }
    }
}
