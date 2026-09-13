using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Silksong_Rando_Logic_Manager.Data;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20260906180000_ReplaceCheckApworldWithLocationType")]
public partial class ReplaceCheckApworldWithLocationType : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "__CheckLocations_location_type",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                InGameId = table.Column<string>(type: "TEXT", nullable: true, collation: "NOCASE"),
                InGamePositionX = table.Column<double>(type: "REAL", nullable: true),
                InGamePositionY = table.Column<double>(type: "REAL", nullable: true),
                InGamePositionZ = table.Column<double>(type: "REAL", nullable: true),
                AnnotationSceneUnitX = table.Column<double>(type: "REAL", nullable: true),
                AnnotationSceneUnitY = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionX = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionY = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionZ = table.Column<double>(type: "REAL", nullable: true),
                SubroomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                Requirements = table.Column<string>(type: "TEXT", nullable: false),
                RequirementsParseSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                Notes = table.Column<string>(type: "TEXT", nullable: false),
                LocationType = table.Column<string>(type: "TEXT", nullable: true),
                EnableAnnotation = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                ResolvedSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                IsTodo = table.Column<bool>(type: "INTEGER", nullable: false),
                IsVerified = table.Column<bool>(type: "INTEGER", nullable: true),
                IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
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

        migrationBuilder.Sql("""
            INSERT INTO "__CheckLocations_location_type"
                ("Id", "RoomId", "FriendlyName", "InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ",
                 "AnnotationSceneUnitX", "AnnotationSceneUnitY", "LocalPositionX", "LocalPositionY", "LocalPositionZ",
                 "SubroomReferenceText", "Requirements", "RequirementsParseSucceeded", "Notes", "LocationType",
                 "EnableAnnotation", "ResolvedSubroomId", "SortOrder", "IsTodo", "IsVerified", "IsArchived",
                 "ArchivedUtc", "CreatedUtc", "UpdatedUtc")
            SELECT "Id", "RoomId", "FriendlyName", "InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ",
                   "AnnotationSceneUnitX", "AnnotationSceneUnitY", "LocalPositionX", "LocalPositionY", "LocalPositionZ",
                   "SubroomReferenceText", "Requirements", "RequirementsParseSucceeded", "Notes", NULL,
                   "EnableAnnotation", "ResolvedSubroomId", "SortOrder", "IsTodo", "IsVerified", "IsArchived",
                   "ArchivedUtc", "CreatedUtc", "UpdatedUtc"
            FROM "CheckLocations";
            """);

        migrationBuilder.DropTable(name: "CheckLocations");
        migrationBuilder.RenameTable(name: "__CheckLocations_location_type", newName: "CheckLocations");
        migrationBuilder.CreateIndex(name: "IX_CheckLocations_ResolvedSubroomId", table: "CheckLocations", column: "ResolvedSubroomId");
        migrationBuilder.CreateIndex(name: "IX_CheckLocations_RoomId", table: "CheckLocations", column: "RoomId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "__CheckLocations_apworld",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                InGameId = table.Column<string>(type: "TEXT", nullable: true, collation: "NOCASE"),
                InGamePositionX = table.Column<double>(type: "REAL", nullable: true),
                InGamePositionY = table.Column<double>(type: "REAL", nullable: true),
                InGamePositionZ = table.Column<double>(type: "REAL", nullable: true),
                AnnotationSceneUnitX = table.Column<double>(type: "REAL", nullable: true),
                AnnotationSceneUnitY = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionX = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionY = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionZ = table.Column<double>(type: "REAL", nullable: true),
                SubroomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                Requirements = table.Column<string>(type: "TEXT", nullable: false),
                RequirementsParseSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                Notes = table.Column<string>(type: "TEXT", nullable: false),
                IsIncludedInApworld = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                EnableAnnotation = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                ResolvedSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                IsTodo = table.Column<bool>(type: "INTEGER", nullable: false),
                IsVerified = table.Column<bool>(type: "INTEGER", nullable: true),
                IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                ArchivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
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

        migrationBuilder.Sql("""
            INSERT INTO "__CheckLocations_apworld"
                ("Id", "RoomId", "FriendlyName", "InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ",
                 "AnnotationSceneUnitX", "AnnotationSceneUnitY", "LocalPositionX", "LocalPositionY", "LocalPositionZ",
                 "SubroomReferenceText", "Requirements", "RequirementsParseSucceeded", "Notes", "IsIncludedInApworld",
                 "EnableAnnotation", "ResolvedSubroomId", "SortOrder", "IsTodo", "IsVerified", "IsArchived",
                 "ArchivedUtc", "CreatedUtc", "UpdatedUtc")
            SELECT "Id", "RoomId", "FriendlyName", "InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ",
                   "AnnotationSceneUnitX", "AnnotationSceneUnitY", "LocalPositionX", "LocalPositionY", "LocalPositionZ",
                   "SubroomReferenceText", "Requirements", "RequirementsParseSucceeded", "Notes", 1,
                   "EnableAnnotation", "ResolvedSubroomId", "SortOrder", "IsTodo", "IsVerified", "IsArchived",
                   "ArchivedUtc", "CreatedUtc", "UpdatedUtc"
            FROM "CheckLocations";
            """);

        migrationBuilder.DropTable(name: "CheckLocations");
        migrationBuilder.RenameTable(name: "__CheckLocations_apworld", newName: "CheckLocations");
        migrationBuilder.CreateIndex(name: "IX_CheckLocations_ResolvedSubroomId", table: "CheckLocations", column: "ResolvedSubroomId");
        migrationBuilder.CreateIndex(name: "IX_CheckLocations_RoomId", table: "CheckLocations", column: "RoomId");
    }
}
