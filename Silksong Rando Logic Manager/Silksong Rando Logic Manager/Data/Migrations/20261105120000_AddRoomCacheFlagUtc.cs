using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

public partial class AddRoomCacheFlagUtc : Migration
{
    // Frozen schema inventory. Future column changes must maintain these predicates.
    private const string Audit = "Id CreatedUtc UpdatedUtc IsArchived ArchivedUtc";
    private const string Wall = "(621355968000000000 + CAST(strftime('%s','now') AS INTEGER)*10000000 + CAST(substr(strftime('%f','now'),4,3) AS INTEGER)*10000)";
    private const string Maximum = "3155378975999999999";
    private static string WallChecked => $"CASE WHEN typeof({Wall}) != 'integer' OR {Wall} NOT BETWEEN 1 AND {Maximum} THEN RAISE(ABORT, 'Invalid room freshness clock') ELSE {Wall} END";
    private static string Advance => $"CASE WHEN typeof(CacheFlagUtc) != 'integer' OR CacheFlagUtc NOT BETWEEN 0 AND {Maximum} OR CacheFlagUtc = {Maximum} OR typeof({Wall}) != 'integer' OR {Wall} NOT BETWEEN 1 AND {Maximum} THEN RAISE(ABORT, 'Room freshness overflow or invalid clock') ELSE max(CacheFlagUtc + 1, {Wall}) END";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Rooms ADD COLUMN CacheFlagUtc INTEGER NOT NULL DEFAULT 0 CONSTRAINT CK_Rooms_CacheFlagUtc CHECK(typeof(CacheFlagUtc) = 'integer' AND CacheFlagUtc BETWEEN 0 AND 3155378975999999999);");
        // RAISE is trigger-only: a temporary validation trigger protects backfill too.
        migrationBuilder.Sql($"CREATE TRIGGER CF_BackfillGuard BEFORE UPDATE OF CacheFlagUtc ON Rooms WHEN typeof({Wall}) != 'integer' OR {Wall} NOT BETWEEN 1 AND {Maximum} BEGIN SELECT RAISE(ABORT, 'Invalid room freshness clock'); END;");
        migrationBuilder.Sql($"UPDATE Rooms SET CacheFlagUtc = {Wall};");
        migrationBuilder.Sql("DROP TRIGGER CF_BackfillGuard;");

        Install(migrationBuilder, "Rooms", Audit + " RoomGroupId ReferenceId FriendlyName InGameId Contributors Comments SceneMinX SceneMinY SceneMaxX SceneMaxY SceneImagePixelsPerUnitX SceneImagePixelsPerUnitY SceneImagePixelOffsetX SceneImagePixelOffsetY UseSceneImage UseUploadedSceneImage SceneImageOriginalHash SceneImageCropHash IsSceneImageStale SortOrder", true);
        Install(migrationBuilder, "Subrooms", Audit + " RoomId ReferenceId FriendlyName Notes SceneUnitX SceneUnitY SceneUnitWidth SceneUnitHeight EnableAnnotation SortOrder", false);
        Install(migrationBuilder, "RoomTransitions", Audit + " RoomId Alias FriendlyName InGameId IsUnsafeArrival InGamePositionX InGamePositionY InGamePositionZ AnnotationSceneUnitX AnnotationSceneUnitY EnableAnnotation LocalPositionX LocalPositionY LocalPositionZ SourceSubroomReferenceText DestinationRoomReferenceText DestinationTransitionAliasText Requirements RequirementsParseSucceeded Notes ResolvedSourceSubroomId ResolvedDestinationRoomId ResolvedDestinationTransitionId SortOrder IsTodo IsVerified", false);
        Install(migrationBuilder, "SubroomConnections", Audit + " RoomId Alias FriendlyName SourceSubroomReferenceText DestinationSubroomReferenceText Requirements RequirementsParseSucceeded Notes EnableAnnotation SceneUnitX SceneUnitY ResolvedSourceSubroomId ResolvedDestinationSubroomId SortOrder IsTodo IsVerified", false);
        Install(migrationBuilder, "CheckLocations", Audit + " RoomId FriendlyName InGameId InGamePositionX InGamePositionY InGamePositionZ AnnotationSceneUnitX AnnotationSceneUnitY LocalPositionX LocalPositionY LocalPositionZ SubroomReferenceText Requirements RequirementsParseSucceeded Notes LocationType EnableAnnotation ResolvedSubroomId SortOrder IsTodo IsVerified", false);
    }

    private static void Install(MigrationBuilder migrationBuilder, string table, string columns, bool room)
    {
        var changed = string.Join(" OR ", columns.Split(' ').Select(column =>
            $"(typeof(OLD.\"{column}\") != typeof(NEW.\"{column}\") OR OLD.\"{column}\" COLLATE BINARY IS NOT NEW.\"{column}\" COLLATE BINARY)"));
        migrationBuilder.Sql($"CREATE TRIGGER CF_{table}_Insert AFTER INSERT ON \"{table}\" BEGIN UPDATE Rooms SET CacheFlagUtc = {(room ? WallChecked : Advance)} WHERE Id = NEW.\"{(room ? "Id" : "RoomId")}\"; END;");
        migrationBuilder.Sql($"CREATE TRIGGER CF_{table}_Update AFTER UPDATE ON \"{table}\" WHEN {changed} BEGIN UPDATE Rooms SET CacheFlagUtc = {Advance} WHERE Id IN (OLD.\"{(room ? "Id" : "RoomId")}\", NEW.\"{(room ? "Id" : "RoomId")}\"); END;");
        if (!room)
            migrationBuilder.Sql($"CREATE TRIGGER CF_{table}_Delete AFTER DELETE ON \"{table}\" BEGIN UPDATE Rooms SET CacheFlagUtc = {Advance} WHERE Id = OLD.RoomId; END;");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Room freshness is patch-forward only; restore a compatible backup instead of downgrading.");
}
