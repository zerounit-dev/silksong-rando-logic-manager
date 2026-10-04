using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261003120000_AddTransitionUnsafeArrival")]
public partial class AddTransitionUnsafeArrival : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "IsUnsafeArrival", table: "RoomTransitions",
            type: "INTEGER", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A complete rebuild avoids SQLite's unsupported DropColumnOperation.
        // Disable foreign keys outside the transaction so inbound/self references
        // survive dropping the old table. The self FK names the final table.
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
        migrationBuilder.CreateTable(
            name: "__RoomTransitions_without_unsafe_arrival",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RoomId = table.Column<Guid>(type: "TEXT", nullable: false),
                Alias = table.Column<string>(type: "TEXT", nullable: false),
                FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                InGameId = table.Column<string>(type: "TEXT", nullable: true, collation: "NOCASE"),
                InGamePositionX = table.Column<double>(type: "REAL", nullable: true),
                InGamePositionY = table.Column<double>(type: "REAL", nullable: true),
                InGamePositionZ = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionX = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionY = table.Column<double>(type: "REAL", nullable: true),
                LocalPositionZ = table.Column<double>(type: "REAL", nullable: true),
                AnnotationSceneUnitX = table.Column<double>(type: "REAL", nullable: true),
                AnnotationSceneUnitY = table.Column<double>(type: "REAL", nullable: true),
                EnableAnnotation = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                SourceSubroomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                DestinationRoomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                DestinationTransitionAliasText = table.Column<string>(type: "TEXT", nullable: true),
                Requirements = table.Column<string>(type: "TEXT", nullable: false),
                RequirementsParseSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                Notes = table.Column<string>(type: "TEXT", nullable: false),
                ResolvedSourceSubroomId = table.Column<Guid>(type: "TEXT", nullable: true),
                ResolvedDestinationRoomId = table.Column<Guid>(type: "TEXT", nullable: true),
                ResolvedDestinationTransitionId = table.Column<Guid>(type: "TEXT", nullable: true),
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
                table.PrimaryKey("PK_RoomTransitions", x => x.Id);
                table.ForeignKey("FK_RoomTransitions_Rooms_RoomId", x => x.RoomId, "Rooms", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_RoomTransitions_Subrooms_ResolvedSourceSubroomId", x => x.ResolvedSourceSubroomId, "Subrooms", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_RoomTransitions_Rooms_ResolvedDestinationRoomId", x => x.ResolvedDestinationRoomId, "Rooms", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_RoomTransitions_RoomTransitions_ResolvedDestinationTransitionId", x => x.ResolvedDestinationTransitionId, "RoomTransitions", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.Sql("""
            INSERT INTO "__RoomTransitions_without_unsafe_arrival"
                ("Id", "RoomId", "Alias", "FriendlyName", "InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ",
                 "LocalPositionX", "LocalPositionY", "LocalPositionZ", "AnnotationSceneUnitX", "AnnotationSceneUnitY", "EnableAnnotation",
                 "SourceSubroomReferenceText", "DestinationRoomReferenceText", "DestinationTransitionAliasText", "Requirements", "RequirementsParseSucceeded", "Notes",
                 "ResolvedSourceSubroomId", "ResolvedDestinationRoomId", "ResolvedDestinationTransitionId", "SortOrder", "IsTodo", "IsVerified",
                 "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc")
            SELECT "Id", "RoomId", "Alias", "FriendlyName", "InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ",
                   "LocalPositionX", "LocalPositionY", "LocalPositionZ", "AnnotationSceneUnitX", "AnnotationSceneUnitY", "EnableAnnotation",
                   "SourceSubroomReferenceText", "DestinationRoomReferenceText", "DestinationTransitionAliasText", "Requirements", "RequirementsParseSucceeded", "Notes",
                   "ResolvedSourceSubroomId", "ResolvedDestinationRoomId", "ResolvedDestinationTransitionId", "SortOrder", "IsTodo", "IsVerified",
                   "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc"
            FROM "RoomTransitions";
            """);
        migrationBuilder.DropTable(name: "RoomTransitions");
        migrationBuilder.RenameTable(name: "__RoomTransitions_without_unsafe_arrival", newName: "RoomTransitions");
        migrationBuilder.CreateIndex(name: "IX_RoomTransitions_RoomId", table: "RoomTransitions", column: "RoomId");
        migrationBuilder.CreateIndex(name: "IX_RoomTransitions_ResolvedSourceSubroomId", table: "RoomTransitions", column: "ResolvedSourceSubroomId");
        migrationBuilder.CreateIndex(name: "IX_RoomTransitions_ResolvedDestinationRoomId", table: "RoomTransitions", column: "ResolvedDestinationRoomId");
        migrationBuilder.CreateIndex(name: "IX_RoomTransitions_ResolvedDestinationTransitionId", table: "RoomTransitions", column: "ResolvedDestinationTransitionId");
        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
}
