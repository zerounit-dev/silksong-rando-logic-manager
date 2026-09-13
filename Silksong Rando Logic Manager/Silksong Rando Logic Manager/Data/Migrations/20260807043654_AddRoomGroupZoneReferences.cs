using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomGroupZoneReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedMapZoneId",
                table: "RoomGroups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZoneReferenceText",
                table: "RoomGroups",
                type: "TEXT",
                nullable: true,
                collation: "NOCASE");

            migrationBuilder.CreateIndex(
                name: "IX_RoomGroups_ResolvedMapZoneId",
                table: "RoomGroups",
                column: "ResolvedMapZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_RoomGroups_MapZones_ResolvedMapZoneId",
                table: "RoomGroups",
                column: "ResolvedMapZoneId",
                principalTable: "MapZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RoomGroups_MapZones_ResolvedMapZoneId",
                table: "RoomGroups");

            migrationBuilder.DropIndex(
                name: "IX_RoomGroups_ResolvedMapZoneId",
                table: "RoomGroups");

            migrationBuilder.DropColumn(
                name: "ResolvedMapZoneId",
                table: "RoomGroups");

            migrationBuilder.DropColumn(
                name: "ZoneReferenceText",
                table: "RoomGroups");
        }
    }
}
