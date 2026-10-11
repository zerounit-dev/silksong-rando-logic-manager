using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261009120000_ReplaceRoomSceneBoundsAndMapping")]
public sealed class ReplaceRoomSceneBoundsAndMapping : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>("SceneMinX", "Rooms", "REAL", nullable: false, defaultValue: 0d);
        migrationBuilder.AddColumn<double>("SceneMinY", "Rooms", "REAL", nullable: false, defaultValue: 0d);
        migrationBuilder.AddColumn<double>("SceneMaxX", "Rooms", "REAL", nullable: true);
        migrationBuilder.AddColumn<double>("SceneMaxY", "Rooms", "REAL", nullable: true);
        migrationBuilder.AddColumn<double>("SceneImagePixelsPerUnitX", "Rooms", "REAL", nullable: true);
        migrationBuilder.AddColumn<double>("SceneImagePixelsPerUnitY", "Rooms", "REAL", nullable: true);
        migrationBuilder.AddColumn<double>("SceneImagePanX", "Rooms", "REAL", nullable: true);
        migrationBuilder.AddColumn<double>("SceneImagePanY", "Rooms", "REAL", nullable: true);
        migrationBuilder.AddColumn<bool>("SceneImageUseArtwork", "Rooms", "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.Sql("""
            UPDATE "Rooms" SET "SceneMaxX" = "SceneUnitWidth", "SceneMaxY" = "SceneUnitHeight";
            UPDATE "Rooms" SET
                "SceneImagePixelsPerUnitX" = 20500.0 * "SceneImageScaleXPercent" / (100.0 * "SceneUnitWidth"),
                "SceneImagePixelsPerUnitY" = 15000.0 * "SceneImageScaleYPercent" / (100.0 * "SceneUnitHeight"),
                "SceneImagePanX" = "SceneUnitWidth" * ((50.0 - "SceneImagePanXPercent") / "SceneImageScaleXPercent" - 0.5),
                "SceneImagePanY" = "SceneUnitHeight" * ((50.0 + "SceneImagePanYPercent") / "SceneImageScaleYPercent" - 0.5)
            WHERE "SceneUnitWidth" > 0 AND "SceneUnitWidth" <= 1.7976931348623157e308
              AND "SceneUnitHeight" > 0 AND "SceneUnitHeight" <= 1.7976931348623157e308
              AND "SceneImageScaleXPercent" > 0 AND "SceneImageScaleXPercent" <= 1.7976931348623157e308
              AND "SceneImageScaleYPercent" > 0 AND "SceneImageScaleYPercent" <= 1.7976931348623157e308
              AND abs("SceneImagePanXPercent") <= 1.7976931348623157e308
              AND abs("SceneImagePanYPercent") <= 1.7976931348623157e308;
            UPDATE "Rooms" SET "SceneImagePixelsPerUnitX" = NULL, "SceneImagePixelsPerUnitY" = NULL,
                "SceneImagePanX" = NULL, "SceneImagePanY" = NULL, "IsSceneImageStale" = 1
            WHERE ("SceneImageScaleXPercent" IS NOT NULL OR "SceneImageScaleYPercent" IS NOT NULL
                OR "SceneImagePanXPercent" IS NOT NULL OR "SceneImagePanYPercent" IS NOT NULL)
              AND NOT coalesce("SceneImagePixelsPerUnitX" > 0 AND "SceneImagePixelsPerUnitX" <= 1.7976931348623157e308
                AND "SceneImagePixelsPerUnitY" > 0 AND "SceneImagePixelsPerUnitY" <= 1.7976931348623157e308
                AND abs("SceneImagePanX") <= 1.7976931348623157e308
                AND abs("SceneImagePanY") <= 1.7976931348623157e308, 0);
            """);
        // SQLite's native DROP COLUMN retains room identity, constraints and child FKs.
        foreach (var column in new[] { "SceneUnitWidth", "SceneUnitHeight", "SceneImageScaleXPercent", "SceneImageScaleYPercent", "SceneImagePanXPercent", "SceneImagePanYPercent" })
            migrationBuilder.Sql($"ALTER TABLE \"Rooms\" DROP COLUMN \"{column}\";");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("This scene bounds and mapping migration cannot be downgraded. Recover using an existing database backup.");
}
