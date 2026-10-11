using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261010120000_UseSourcePixelOffsetsForSceneImage")]
public sealed class UseSourcePixelOffsetsForSceneImage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Validate every supplied axis before modifying Rooms. This temporary guard
        // belongs to the existing migration transaction, not the persisted model.
        // Zero/negative finite scales are convertible even when unusable for capture.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "RIX1PixelOffsetGuard" (
                "Convertible" INTEGER NOT NULL CHECK ("Convertible" = 1)
            );
            INSERT INTO "RIX1PixelOffsetGuard" ("Convertible")
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM "Rooms"
                WHERE ("SceneImagePanX" IS NOT NULL AND NOT coalesce(
                    abs("SceneImagePanX") <= 1.7976931348623157e308
                    AND abs("SceneImagePixelsPerUnitX") <= 1.7976931348623157e308
                    AND abs("SceneImagePanX" * "SceneImagePixelsPerUnitX") <= 1.7976931348623157e308, 0))
                   OR ("SceneImagePanY" IS NOT NULL AND NOT coalesce(
                    abs("SceneImagePanY") <= 1.7976931348623157e308
                    AND abs("SceneImagePixelsPerUnitY") <= 1.7976931348623157e308
                    AND abs("SceneImagePanY" * "SceneImagePixelsPerUnitY") <= 1.7976931348623157e308, 0))
            ) THEN 1 ELSE 0 END;
            UPDATE "Rooms" SET
                "SceneImagePanX" = CASE WHEN "SceneImagePanX" IS NULL THEN NULL
                    ELSE "SceneImagePanX" * "SceneImagePixelsPerUnitX" END,
                "SceneImagePanY" = CASE WHEN "SceneImagePanY" IS NULL THEN NULL
                    ELSE "SceneImagePanY" * "SceneImagePixelsPerUnitY" END;
            ALTER TABLE "Rooms" RENAME COLUMN "SceneImagePanX" TO "SceneImagePixelOffsetX";
            ALTER TABLE "Rooms" RENAME COLUMN "SceneImagePanY" TO "SceneImagePixelOffsetY";
            ALTER TABLE "Rooms" RENAME COLUMN "SceneImageUseArtwork" TO "UseSceneImage";
            DROP TABLE "temp"."RIX1PixelOffsetGuard";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("This scene image pixel-offset migration cannot be downgraded. Recover using an existing database backup.");
}
