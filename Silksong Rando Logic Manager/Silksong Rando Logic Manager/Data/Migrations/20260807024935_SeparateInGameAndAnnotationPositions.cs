using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeparateInGameAndAnnotationPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "WorldPositionZ",
                table: "RoomTransitions",
                newName: "InGamePositionZ");

            migrationBuilder.RenameColumn(
                name: "WorldPositionY",
                table: "RoomTransitions",
                newName: "InGamePositionY");

            migrationBuilder.RenameColumn(
                name: "WorldPositionX",
                table: "RoomTransitions",
                newName: "InGamePositionX");

            migrationBuilder.RenameColumn(
                name: "WorldPositionZ",
                table: "CheckLocations",
                newName: "InGamePositionZ");

            migrationBuilder.RenameColumn(
                name: "WorldPositionY",
                table: "CheckLocations",
                newName: "InGamePositionY");

            migrationBuilder.RenameColumn(
                name: "WorldPositionX",
                table: "CheckLocations",
                newName: "InGamePositionX");

            migrationBuilder.AddColumn<double>(
                name: "AnnotationSceneUnitX",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AnnotationSceneUnitY",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AnnotationSceneUnitX",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AnnotationSceneUnitY",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnotationSceneUnitX",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "AnnotationSceneUnitY",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "AnnotationSceneUnitX",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "AnnotationSceneUnitY",
                table: "CheckLocations");

            migrationBuilder.RenameColumn(
                name: "InGamePositionZ",
                table: "RoomTransitions",
                newName: "WorldPositionZ");

            migrationBuilder.RenameColumn(
                name: "InGamePositionY",
                table: "RoomTransitions",
                newName: "WorldPositionY");

            migrationBuilder.RenameColumn(
                name: "InGamePositionX",
                table: "RoomTransitions",
                newName: "WorldPositionX");

            migrationBuilder.RenameColumn(
                name: "InGamePositionZ",
                table: "CheckLocations",
                newName: "WorldPositionZ");

            migrationBuilder.RenameColumn(
                name: "InGamePositionY",
                table: "CheckLocations",
                newName: "WorldPositionY");

            migrationBuilder.RenameColumn(
                name: "InGamePositionX",
                table: "CheckLocations",
                newName: "WorldPositionX");
        }
    }
}
