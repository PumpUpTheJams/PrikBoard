using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrikBoard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShortId = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NextCardNumber = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Rules = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    RulesUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    PublicShowsPeople = table.Column<bool>(type: "boolean", nullable: false),
                    ShowCursors = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscordPreviewMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Sources = table.Column<List<string>>(type: "text[]", nullable: false),
                    CardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    BoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordPreviewMessages", x => x.MessageId);
                });

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

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BoardTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedFromCardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardTags_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Buckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Collapsed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StackedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buckets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Buckets_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Principals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastBoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: true),
                    AvatarUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvatarOptedOut = table.Column<bool>(type: "boolean", nullable: true),
                    AvatarFileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Principals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Principals_Boards_LastBoardId",
                        column: x => x.LastBoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Principals_Principals_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Principals_StoredFiles_AvatarFileId",
                        column: x => x.AvatarFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MaxRole = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BoardIds = table.Column<List<Guid>>(type: "uuid[]", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessTokens_Principals_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessTokens_Principals_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppSignInGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSignInGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppSignInGrants_Principals_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    BucketId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    AssigneeIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Components = table.Column<string>(type: "jsonb", nullable: false),
                    AgentWorking = table.Column<bool>(type: "boolean", nullable: false),
                    AgentWorkingForId = table.Column<Guid>(type: "uuid", nullable: true),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cards_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Cards_Buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "Buckets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Cards_Principals_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DiscordAccounts",
                columns: table => new
                {
                    DiscordUserHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordAccounts", x => x.DiscordUserHash);
                    table.ForeignKey(
                        name: "FK_DiscordAccounts_Principals_UserId",
                        column: x => x.UserId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscordBotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ProtectedBotToken = table.Column<string>(type: "text", nullable: true),
                    ApplicationId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BotName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PublicUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordBotSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscordBotSettings_Principals_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DiscordGuilds",
                columns: table => new
                {
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordGuilds", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_DiscordGuilds_Principals_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscordGuilds_Principals_ConnectedById",
                        column: x => x.ConnectedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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

            migrationBuilder.CreateTable(
                name: "CardChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ById = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Entries = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardChanges_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CardChanges_Principals_ById",
                        column: x => x.ById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Drawings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    BucketId = table.Column<Guid>(type: "uuid", nullable: true),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Colors = table.Column<string[]>(type: "text[]", nullable: false),
                    PointCount = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StackedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drawings", x => x.Id);
                    table.CheckConstraint("CK_Drawings_CardOrBucket", "\"CardId\" IS NULL OR \"BucketId\" IS NULL");
                    table.ForeignKey(
                        name: "FK_Drawings_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Drawings_Buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "Buckets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Drawings_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Drawings_Principals_AddedById",
                        column: x => x.AddedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    BucketId = table.Column<Guid>(type: "uuid", nullable: true),
                    PixelWidth = table.Column<int>(type: "integer", nullable: false),
                    PixelHeight = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Caption = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Images", x => x.Id);
                    table.CheckConstraint("CK_Images_CardOrBucket", "\"CardId\" IS NULL OR \"BucketId\" IS NULL");
                    table.ForeignKey(
                        name: "FK_Images_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Images_Buckets_BucketId",
                        column: x => x.BucketId,
                        principalTable: "Buckets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Images_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Images_Principals_AddedById",
                        column: x => x.AddedById,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Images_StoredFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    X = table.Column<int>(type: "integer", nullable: true),
                    Y = table.Column<int>(type: "integer", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Caption = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cap = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AccessTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageUploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageUploads_AccessTokens_AccessTokenId",
                        column: x => x.AccessTokenId,
                        principalTable: "AccessTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageUploads_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageUploads_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageUploads_Principals_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageUploads_Principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalTable: "Principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscordBoardChannels",
                columns: table => new
                {
                    GuildId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WholeServer = table.Column<bool>(type: "boolean", nullable: false),
                    Channels = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordBoardChannels", x => new { x.GuildId, x.BoardId });
                    table.ForeignKey(
                        name: "FK_DiscordBoardChannels_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscordBoardChannels_DiscordGuilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "DiscordGuilds",
                        principalColumn: "GuildId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DrawingContents",
                columns: table => new
                {
                    DrawingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Strokes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawingContents", x => x.DrawingId);
                    table.ForeignKey(
                        name: "FK_DrawingContents_Drawings_DrawingId",
                        column: x => x.DrawingId,
                        principalTable: "Drawings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_AgentId",
                table: "AccessTokens",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_OwnerId",
                table: "AccessTokens",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessTokens_TokenHash",
                table: "AccessTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppSignInGrants_CodeHash",
                table: "AppSignInGrants",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppSignInGrants_OwnerId",
                table: "AppSignInGrants",
                column: "OwnerId");

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
                name: "IX_Boards_ShortId",
                table: "Boards",
                column: "ShortId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardTags_BoardId_Name",
                table: "BoardTags",
                columns: new[] { "BoardId", "Name" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Buckets_BoardId",
                table: "Buckets",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_CardChanges_At",
                table: "CardChanges",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_CardChanges_ById",
                table: "CardChanges",
                column: "ById");

            migrationBuilder.CreateIndex(
                name: "IX_CardChanges_CardId_At",
                table: "CardChanges",
                columns: new[] { "CardId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Cards_BoardId_Number",
                table: "Cards",
                columns: new[] { "BoardId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cards_BucketId",
                table: "Cards",
                column: "BucketId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_CreatedById",
                table: "Cards",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordAccounts_UserId",
                table: "DiscordAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordBoardChannels_BoardId",
                table: "DiscordBoardChannels",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordBotSettings_UpdatedById",
                table: "DiscordBotSettings",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordGuilds_AgentId",
                table: "DiscordGuilds",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordGuilds_ConnectedById",
                table: "DiscordGuilds",
                column: "ConnectedById");

            migrationBuilder.CreateIndex(
                name: "IX_DiscordPreviewMessages_PostedAt",
                table: "DiscordPreviewMessages",
                column: "PostedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Drawings_AddedById",
                table: "Drawings",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_Drawings_BoardId",
                table: "Drawings",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Drawings_BucketId",
                table: "Drawings",
                column: "BucketId");

            migrationBuilder.CreateIndex(
                name: "IX_Drawings_CardId",
                table: "Drawings",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_AddedById",
                table: "Images",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_Images_BoardId",
                table: "Images",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_BucketId",
                table: "Images",
                column: "BucketId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_CardId",
                table: "Images",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_FileId",
                table: "Images",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_AccessTokenId",
                table: "ImageUploads",
                column: "AccessTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_BoardId",
                table: "ImageUploads",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_CardId",
                table: "ImageUploads",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_MemberId",
                table: "ImageUploads",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_PrincipalId",
                table: "ImageUploads",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_TokenHash",
                table: "ImageUploads",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Principals_AvatarFileId",
                table: "Principals",
                column: "AvatarFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Principals_Issuer_ClientId",
                table: "Principals",
                columns: new[] { "Issuer", "ClientId" },
                unique: true,
                filter: "\"Kind\" = 'Agent'");

            migrationBuilder.CreateIndex(
                name: "IX_Principals_LastBoardId",
                table: "Principals",
                column: "LastBoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Principals_OwnerId",
                table: "Principals",
                column: "OwnerId");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSignInGrants");

            migrationBuilder.DropTable(
                name: "BoardInvites");

            migrationBuilder.DropTable(
                name: "BoardMembers");

            migrationBuilder.DropTable(
                name: "BoardTags");

            migrationBuilder.DropTable(
                name: "CardChanges");

            migrationBuilder.DropTable(
                name: "DiscordAccounts");

            migrationBuilder.DropTable(
                name: "DiscordBoardChannels");

            migrationBuilder.DropTable(
                name: "DiscordBotSettings");

            migrationBuilder.DropTable(
                name: "DiscordPreviewMessages");

            migrationBuilder.DropTable(
                name: "DrawingContents");

            migrationBuilder.DropTable(
                name: "Images");

            migrationBuilder.DropTable(
                name: "ImageUploads");

            migrationBuilder.DropTable(
                name: "SignInProviders");

            migrationBuilder.DropTable(
                name: "UserIdentities");

            migrationBuilder.DropTable(
                name: "DiscordGuilds");

            migrationBuilder.DropTable(
                name: "Drawings");

            migrationBuilder.DropTable(
                name: "AccessTokens");

            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropTable(
                name: "Buckets");

            migrationBuilder.DropTable(
                name: "Principals");

            migrationBuilder.DropTable(
                name: "Boards");

            migrationBuilder.DropTable(
                name: "StoredFiles");
        }
    }
}
