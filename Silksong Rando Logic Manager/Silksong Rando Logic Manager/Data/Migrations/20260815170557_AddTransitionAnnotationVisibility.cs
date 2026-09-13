using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransitionAnnotationVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAnnotation",
                table: "RoomTransitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Preserve the legacy visible effective markers, but never fabricate one
            // from a partial, NaN, or infinite pair.  Manual overrides take
            // precedence over imported game coordinates.
            migrationBuilder.Sql("""
                UPDATE RoomTransitions
                SET EnableAnnotation = 1
                WHERE
                    (AnnotationSceneUnitX IS NOT NULL AND AnnotationSceneUnitY IS NOT NULL
                     AND AnnotationSceneUnitX = AnnotationSceneUnitX AND AnnotationSceneUnitY = AnnotationSceneUnitY
                      AND abs(AnnotationSceneUnitX) <= 1.7976931348623157e308
                      AND abs(AnnotationSceneUnitY) <= 1.7976931348623157e308)
                    OR
                    (AnnotationSceneUnitX IS NULL AND AnnotationSceneUnitY IS NULL
                     AND InGamePositionX IS NOT NULL AND InGamePositionY IS NOT NULL
                     AND InGamePositionX = InGamePositionX AND InGamePositionY = InGamePositionY
                      AND abs(InGamePositionX) <= 1.7976931348623157e308
                      AND abs(InGamePositionY) <= 1.7976931348623157e308)
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAnnotation",
                table: "RoomTransitions");
        }
    }
}
