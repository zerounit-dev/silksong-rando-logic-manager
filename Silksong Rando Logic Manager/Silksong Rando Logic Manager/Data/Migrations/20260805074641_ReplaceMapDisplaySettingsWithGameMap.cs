using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceMapDisplaySettingsWithGameMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapDisplaySettings");

            migrationBuilder.AddColumn<double>(
                name: "MapCoordinateX",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MapCoordinateY",
                table: "Rooms",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GameMapConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameMapLeft = table.Column<double>(type: "REAL", nullable: true),
                    GameMapRight = table.Column<double>(type: "REAL", nullable: true),
                    GameMapBottom = table.Column<double>(type: "REAL", nullable: true),
                    GameMapTop = table.Column<double>(type: "REAL", nullable: true),
                    SceneToMapScale = table.Column<double>(type: "REAL", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameMapConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MapImageCalibrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImageKey = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ImageFileName = table.Column<string>(type: "TEXT", nullable: false),
                    MapHeightScalePercent = table.Column<double>(type: "REAL", nullable: true),
                    MapOffsetXPercent = table.Column<double>(type: "REAL", nullable: true),
                    MapOffsetYPercent = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapImageCalibrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoomMapCoordinates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SceneName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    MapX = table.Column<double>(type: "REAL", nullable: false),
                    MapY = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMapCoordinates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapImageCalibrations_ImageKey",
                table: "MapImageCalibrations",
                column: "ImageKey",
                unique: true);

            migrationBuilder.InsertData("GameMapConfigurations", new[] { "Id", "CreatedUtc", "UpdatedUtc" }, new object[] { new Guid("86d83f2b-0bbc-4bf0-a9ae-ff6a84439c89"), new DateTime(2026, 8, 5, 7, 46, 41, DateTimeKind.Utc), new DateTime(2026, 8, 5, 7, 46, 41, DateTimeKind.Utc) });
            migrationBuilder.InsertData("MapImageCalibrations", new[] { "Id", "ImageKey", "ImageFileName" }, new object[,] { { new Guid("6dbb31c2-1df4-405f-b667-1ea3dd9bfc3a"), "area", "silksong_area_map.png" }, { new Guid("50e7e056-5d70-4221-9a9a-a88d5630a2f5"), "room", "silksong_room_map.png" } });

            migrationBuilder.CreateIndex(
                name: "IX_RoomMapCoordinates_SceneName",
                table: "RoomMapCoordinates",
                column: "SceneName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameMapConfigurations");

            migrationBuilder.DropTable(
                name: "MapImageCalibrations");

            migrationBuilder.DropTable(
                name: "RoomMapCoordinates");

            migrationBuilder.DropColumn(
                name: "MapCoordinateX",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "MapCoordinateY",
                table: "Rooms");

            migrationBuilder.CreateTable(
                name: "MapDisplaySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AreaPixelOffsetX = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelOffsetY = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelsPerWorldX = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelsPerWorldY = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMaxX = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMaxY = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMinX = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMinY = table.Column<double>(type: "REAL", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RoomPixelOffsetX = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelOffsetY = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelsPerWorldX = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelsPerWorldY = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMaxX = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMaxY = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMinX = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMinY = table.Column<double>(type: "REAL", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapDisplaySettings", x => x.Id);
                });
        }
    }
}
