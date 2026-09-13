using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomSceneBounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SceneBoundsMaxX",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SceneBoundsMaxY",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SceneBoundsMinX",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SceneBoundsMinY",
                table: "Rooms",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SceneBoundsMaxX",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneBoundsMaxY",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneBoundsMinX",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "SceneBoundsMinY",
                table: "Rooms");
        }
    }
}
