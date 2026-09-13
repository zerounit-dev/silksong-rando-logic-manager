using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapDisplaySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MapDisplaySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AreaWorldMinX = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMaxX = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMinY = table.Column<double>(type: "REAL", nullable: true),
                    AreaWorldMaxY = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelsPerWorldX = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelsPerWorldY = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelOffsetX = table.Column<double>(type: "REAL", nullable: true),
                    AreaPixelOffsetY = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMinX = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMaxX = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMinY = table.Column<double>(type: "REAL", nullable: true),
                    RoomWorldMaxY = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelsPerWorldX = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelsPerWorldY = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelOffsetX = table.Column<double>(type: "REAL", nullable: true),
                    RoomPixelOffsetY = table.Column<double>(type: "REAL", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapDisplaySettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "MapDisplaySettings",
                columns: new[] { "Id", "CreatedUtc", "UpdatedUtc" },
                values: new object[] { new Guid("818ba4ee-688c-49c1-a68d-61fcb0d7f850"), new DateTime(2026, 8, 5, 4, 22, 47, DateTimeKind.Utc), new DateTime(2026, 8, 5, 4, 22, 47, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapDisplaySettings");
        }
    }
}
