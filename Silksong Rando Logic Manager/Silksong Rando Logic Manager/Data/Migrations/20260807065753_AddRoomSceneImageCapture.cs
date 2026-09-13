using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomSceneImageCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSceneImageStale",
                table: "Rooms",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "SceneImagePanXPercent",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SceneImagePanYPercent",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SceneImageScaleXPercent",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SceneImageScaleYPercent",
                table: "Rooms",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSceneImageStale",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneImagePanXPercent",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneImagePanYPercent",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneImageScaleXPercent",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneImageScaleYPercent",
                table: "Rooms");
        }
    }
}
