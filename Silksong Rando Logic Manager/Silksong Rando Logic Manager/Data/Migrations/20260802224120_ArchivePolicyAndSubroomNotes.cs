using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchivePolicyAndSubroomNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchiveNote",
                table: "SubroomConnections");

            migrationBuilder.DropColumn(
                name: "ArchiveNote",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "ArchiveNote",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "ArchiveNote",
                table: "RoomGroups");

            migrationBuilder.DropColumn(
                name: "ArchivedUtc",
                table: "RoomGroups");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "RoomGroups");

            migrationBuilder.DropColumn(
                name: "ArchiveNote",
                table: "CheckLocations");

            migrationBuilder.DropColumn(
                name: "ArchiveNote",
                table: "Subrooms");

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Subrooms",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Subrooms");

            migrationBuilder.AddColumn<string>(
                name: "ArchiveNote",
                table: "Subrooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveNote",
                table: "SubroomConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveNote",
                table: "RoomTransitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveNote",
                table: "Rooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveNote",
                table: "RoomGroups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedUtc",
                table: "RoomGroups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "RoomGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveNote",
                table: "CheckLocations",
                type: "TEXT",
                nullable: true);
        }
    }
}
