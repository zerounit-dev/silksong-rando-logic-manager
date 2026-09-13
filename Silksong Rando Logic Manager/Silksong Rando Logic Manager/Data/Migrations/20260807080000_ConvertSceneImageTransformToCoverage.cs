using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    [DbContext(typeof(LogicDbContext))]
    [Migration("20260807080000_ConvertSceneImageTransformToCoverage")]
    public partial class ConvertSceneImageTransformToCoverage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Rooms"
                SET "SceneImageScaleXPercent" = 10000.0 / "SceneImageScaleXPercent",
                    "SceneImageScaleYPercent" = 10000.0 / "SceneImageScaleYPercent"
                WHERE "SceneImageScaleXPercent" IS NOT NULL
                  AND "SceneImageScaleYPercent" IS NOT NULL
                  AND "SceneImagePanXPercent" IS NOT NULL
                  AND "SceneImagePanYPercent" IS NOT NULL
                  AND "SceneImageScaleXPercent" > 0
                  AND "SceneImageScaleYPercent" > 0;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Rooms"
                SET "SceneImageScaleXPercent" = 10000.0 / "SceneImageScaleXPercent",
                    "SceneImageScaleYPercent" = 10000.0 / "SceneImageScaleYPercent"
                WHERE "SceneImageScaleXPercent" IS NOT NULL
                  AND "SceneImageScaleYPercent" IS NOT NULL
                  AND "SceneImagePanXPercent" IS NOT NULL
                  AND "SceneImagePanYPercent" IS NOT NULL
                  AND "SceneImageScaleXPercent" > 0
                  AND "SceneImageScaleYPercent" > 0;
                """);
        }
    }
}
