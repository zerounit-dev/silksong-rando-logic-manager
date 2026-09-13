using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedRecordMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InGameId",
                table: "RoomTransitions",
                type: "TEXT",
                nullable: true,
                collation: "NOCASE");

            migrationBuilder.AddColumn<double>(
                name: "LocalPositionX",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocalPositionY",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocalPositionZ",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WorldPositionX",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WorldPositionY",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WorldPositionZ",
                table: "RoomTransitions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InGameId",
                table: "CheckLocations",
                type: "TEXT",
                nullable: true,
                collation: "NOCASE");

            migrationBuilder.AddColumn<double>(
                name: "LocalPositionX",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocalPositionY",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocalPositionZ",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WorldPositionX",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WorldPositionY",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WorldPositionZ",
                table: "CheckLocations",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InGameId",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "LocalPositionX",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "LocalPositionY",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "LocalPositionZ",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "WorldPositionX",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "WorldPositionY",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "WorldPositionZ",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "InGameId",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "LocalPositionX",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "LocalPositionY",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "LocalPositionZ",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "WorldPositionX",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "WorldPositionY",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "WorldPositionZ",
                table: "CheckLocations");
        }
    }
}
