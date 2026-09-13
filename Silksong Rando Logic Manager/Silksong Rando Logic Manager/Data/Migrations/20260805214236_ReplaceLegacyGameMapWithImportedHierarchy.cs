using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceLegacyGameMapWithImportedHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "Maps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InGameId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    MapUnitMinX = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMinY = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMaxX = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMaxY = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MapZones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MapId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InGameId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    MapUnitMinX = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMinY = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMaxX = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMaxY = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapZones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapZones_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MapScenes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MapZoneId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InGameId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    RoomReferenceText = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedRoomId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapScenes_MapZones_MapZoneId",
                        column: x => x.MapZoneId,
                        principalTable: "MapZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MapScenes_Rooms_ResolvedRoomId",
                        column: x => x.ResolvedRoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MapChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MapSceneId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CacheIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    InitialState = table.Column<string>(type: "TEXT", nullable: true),
                    MapUnitMinX = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMinY = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMaxX = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitMaxY = table.Column<double>(type: "REAL", nullable: true),
                    MapUnitZ = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapChunks_MapScenes_MapSceneId",
                        column: x => x.MapSceneId,
                        principalTable: "MapScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapChunks_MapSceneId_CacheIndex",
                table: "MapChunks",
                columns: new[] { "MapSceneId", "CacheIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Maps_InGameId",
                table: "Maps",
                column: "InGameId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapScenes_MapZoneId_InGameId",
                table: "MapScenes",
                columns: new[] { "MapZoneId", "InGameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapScenes_ResolvedRoomId",
                table: "MapScenes",
                column: "ResolvedRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_MapZones_MapId_InGameId",
                table: "MapZones",
                columns: new[] { "MapId", "InGameId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapChunks");

            migrationBuilder.DropTable(
                name: "MapScenes");

            migrationBuilder.DropTable(
                name: "MapZones");

            migrationBuilder.DropTable(
                name: "Maps");

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

            migrationBuilder.CreateTable(
                name: "GameMapConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GameMapBottom = table.Column<double>(type: "REAL", nullable: true),
                    GameMapLeft = table.Column<double>(type: "REAL", nullable: true),
                    GameMapRight = table.Column<double>(type: "REAL", nullable: true),
                    GameMapTop = table.Column<double>(type: "REAL", nullable: true),
                    SceneToMapScale = table.Column<double>(type: "REAL", nullable: true),
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
                    ImageFileName = table.Column<string>(type: "TEXT", nullable: false),
                    ImageKey = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    MapHeightScalePercent = table.Column<double>(type: "REAL", nullable: true, defaultValue: 100.0),
                    MapOffsetXPercent = table.Column<double>(type: "REAL", nullable: true, defaultValue: 0.0),
                    MapOffsetYPercent = table.Column<double>(type: "REAL", nullable: true, defaultValue: 0.0)
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
                    MapX = table.Column<double>(type: "REAL", nullable: false),
                    MapY = table.Column<double>(type: "REAL", nullable: false),
                    SceneName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE")
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

            migrationBuilder.CreateIndex(
                name: "IX_RoomMapCoordinates_SceneName",
                table: "RoomMapCoordinates",
                column: "SceneName",
                unique: true);
        }
    }
}
