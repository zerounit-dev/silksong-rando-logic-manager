using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261110120000_AddRoomCompletionStatus")]
public sealed class AddRoomCompletionStatus : Migration
{
    // Frozen Room-owned predicate from AddRoomCacheFlagUtc plus derived completion.
    // Never use the mutable live EF model to generate migration trigger predicates.
    private const string Columns = "Id CreatedUtc UpdatedUtc IsArchived ArchivedUtc RoomGroupId ReferenceId FriendlyName InGameId Contributors Comments SceneMinX SceneMinY SceneMaxX SceneMaxY SceneImagePixelsPerUnitX SceneImagePixelsPerUnitY SceneImagePixelOffsetX SceneImagePixelOffsetY UseSceneImage UseUploadedSceneImage SceneImageOriginalHash SceneImageCropHash IsSceneImageStale SortOrder CompletionStatus";
    private const string Wall = "(621355968000000000 + CAST(strftime('%s','now') AS INTEGER)*10000000 + CAST(substr(strftime('%f','now'),4,3) AS INTEGER)*10000)";
    private const string Maximum = "3155378975999999999";
    private static string Advance => $"CASE WHEN typeof(CacheFlagUtc) != 'integer' OR CacheFlagUtc NOT BETWEEN 0 AND {Maximum} OR CacheFlagUtc = {Maximum} OR typeof({Wall}) != 'integer' OR {Wall} NOT BETWEEN 1 AND {Maximum} THEN RAISE(ABORT, 'Room freshness overflow or invalid clock') ELSE max(CacheFlagUtc + 1, {Wall}) END";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "CompletionStatus", table: "Rooms",
            type: "INTEGER", nullable: false, defaultValue: 0);
        // ALTER ADD preserves the original insert/child triggers. Only the Room
        // update predicate changes; the clock/overflow/actual-value policy is identical.
        migrationBuilder.Sql("DROP TRIGGER CF_Rooms_Update;");
        var changed = string.Join(" OR ", Columns.Split(' ').Select(column =>
            $"(typeof(OLD.\"{column}\") != typeof(NEW.\"{column}\") OR OLD.\"{column}\" COLLATE BINARY IS NOT NEW.\"{column}\" COLLATE BINARY)"));
        migrationBuilder.Sql($"CREATE TRIGGER CF_Rooms_Update AFTER UPDATE ON Rooms WHEN {changed} BEGIN UPDATE Rooms SET CacheFlagUtc = {Advance} WHERE Id IN (OLD.Id, NEW.Id); END;");
        // Canonical changed-only backfill belongs to normal application startup,
        // after migration and before history initialization, not SQL re-parsing here.
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Stored completion is patch-forward only; restore a compatible backup instead of downgrading.");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildFrozenModel(modelBuilder);

    internal static void BuildFrozenModel(ModelBuilder modelBuilder)
    {
        // The previous target is frozen and self-contained; append only this
        // migration's exact field. Neither target depends on live entity mappings.
        AddRoomCacheFlagUtc.BuildFrozenModel(modelBuilder);
        modelBuilder.Entity("Silksong_Rando_Logic_Manager.Data.Room")
            .Property<AppliedRoomStatus>("CompletionStatus").ValueGeneratedOnAdd()
            .HasConversion<int>().HasColumnType("INTEGER").HasDefaultValue(AppliedRoomStatus.Neutral);
    }
}
