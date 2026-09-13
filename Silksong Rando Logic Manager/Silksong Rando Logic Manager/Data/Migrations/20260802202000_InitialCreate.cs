using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReferenceId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    InGameId = table.Column<string>(type: "TEXT", nullable: true, collation: "NOCASE"),
                    Comments = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rooms_RoomGroups_RoomGroupId",
                        column: x => x.RoomGroupId,
                        principalTable: "RoomGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subrooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReferenceId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subrooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subrooms_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CheckLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    SubroomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                    Requirements = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    IsIncludedInApworld = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApworldLocationReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsTodo = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeedsVerification = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckLocations_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CheckLocations_Subrooms_ResolvedSubroomId",
                        column: x => x.ResolvedSubroomId,
                        principalTable: "Subrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoomTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSubroomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                    DestinationRoomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                    DestinationTransitionAliasText = table.Column<string>(type: "TEXT", nullable: true),
                    Requirements = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedSourceSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedDestinationRoomId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedDestinationTransitionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsTodo = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeedsVerification = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomTransitions", x => x.Id);
                    table.CheckConstraint("CK_RoomTransitions_Alias_Length", "length(Alias) BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_RoomTransitions_RoomTransitions_ResolvedDestinationTransitionId",
                        column: x => x.ResolvedDestinationTransitionId,
                        principalTable: "RoomTransitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoomTransitions_Rooms_ResolvedDestinationRoomId",
                        column: x => x.ResolvedDestinationRoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoomTransitions_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoomTransitions_Subrooms_ResolvedSourceSubroomId",
                        column: x => x.ResolvedSourceSubroomId,
                        principalTable: "Subrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubroomConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSubroomReferenceText = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationSubroomReferenceText = table.Column<string>(type: "TEXT", nullable: false),
                    Requirements = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedSourceSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedDestinationSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsTodo = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeedsVerification = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubroomConnections", x => x.Id);
                    table.CheckConstraint("CK_SubroomConnections_Alias_Length", "length(Alias) BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_SubroomConnections_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubroomConnections_Subrooms_ResolvedDestinationSubroomId",
                        column: x => x.ResolvedDestinationSubroomId,
                        principalTable: "Subrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubroomConnections_Subrooms_ResolvedSourceSubroomId",
                        column: x => x.ResolvedSourceSubroomId,
                        principalTable: "Subrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckLocations_ResolvedSubroomId",
                table: "CheckLocations",
                column: "ResolvedSubroomId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckLocations_RoomId",
                table: "CheckLocations",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_InGameId",
                table: "Rooms",
                column: "InGameId",
                unique: true,
                filter: "IsArchived = 0 AND InGameId IS NOT NULL AND trim(InGameId) <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_ReferenceId",
                table: "Rooms",
                column: "ReferenceId",
                unique: true,
                filter: "IsArchived = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_RoomGroupId",
                table: "Rooms",
                column: "RoomGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomTransitions_ResolvedDestinationRoomId",
                table: "RoomTransitions",
                column: "ResolvedDestinationRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomTransitions_ResolvedDestinationTransitionId",
                table: "RoomTransitions",
                column: "ResolvedDestinationTransitionId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomTransitions_ResolvedSourceSubroomId",
                table: "RoomTransitions",
                column: "ResolvedSourceSubroomId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomTransitions_RoomId",
                table: "RoomTransitions",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_SubroomConnections_ResolvedDestinationSubroomId",
                table: "SubroomConnections",
                column: "ResolvedDestinationSubroomId");

            migrationBuilder.CreateIndex(
                name: "IX_SubroomConnections_ResolvedSourceSubroomId",
                table: "SubroomConnections",
                column: "ResolvedSourceSubroomId");

            migrationBuilder.CreateIndex(
                name: "IX_SubroomConnections_RoomId",
                table: "SubroomConnections",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_Subrooms_RoomId_ReferenceId",
                table: "Subrooms",
                columns: new[] { "RoomId", "ReferenceId" },
                unique: true,
                filter: "IsArchived = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckLocations");

            migrationBuilder.DropTable(
                name: "RoomTransitions");

            migrationBuilder.DropTable(
                name: "SubroomConnections");

            migrationBuilder.DropTable(
                name: "Subrooms");

            migrationBuilder.DropTable(
                name: "Rooms");

            migrationBuilder.DropTable(
                name: "RoomGroups");
        }
    }
}
