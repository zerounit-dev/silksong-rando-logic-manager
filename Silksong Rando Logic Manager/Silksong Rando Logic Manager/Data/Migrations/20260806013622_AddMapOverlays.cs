using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapOverlays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MapOverlays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MapId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                    ImageAssetKey = table.Column<string>(type: "TEXT", nullable: false),
                    ScalePercent = table.Column<double>(type: "REAL", nullable: false, defaultValue: 100.0),
                    LeftOffsetPercent = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0),
                    BottomOffsetPercent = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapOverlays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapOverlays_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapOverlays_MapId",
                table: "MapOverlays",
                column: "MapId");

            migrationBuilder.Sql("""
                INSERT INTO "MapOverlays" ("Id", "MapId", "FriendlyName", "ImageAssetKey", "ScalePercent", "LeftOffsetPercent", "BottomOffsetPercent", "SortOrder")
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))), "Id", 'Area map', 'area-map-hd', 100.0, 0.0, 0.0, 0
                FROM "Maps";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapOverlays");
        }
    }
}
