using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Silksong_Rando_Logic_Manager.Data;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20260922120000_AddExplicitVirtualRoomGroups")]
public partial class AddExplicitVirtualRoomGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsVirtual",
            table: "RoomGroups",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE "RoomGroups"
            SET "IsVirtual" = 1
            WHERE lower("Id") IN (
                '311195a0-074f-4334-aafe-cc3c010b596d',
                '48a3f4c5-6a66-45c1-b8a3-7a5e4ea893ba'
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

        migrationBuilder.CreateTable(
            name: "__RoomGroups_without_virtual",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                FriendlyName = table.Column<string>(type: "TEXT", nullable: false),
                ZoneReferenceText = table.Column<string>(type: "TEXT", nullable: true, collation: "NOCASE"),
                ResolvedMapZoneId = table.Column<Guid>(type: "TEXT", nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoomGroups", x => x.Id);
                table.ForeignKey(
                    name: "FK_RoomGroups_MapZones_ResolvedMapZoneId",
                    column: x => x.ResolvedMapZoneId,
                    principalTable: "MapZones",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql("""
            INSERT INTO "__RoomGroups_without_virtual"
                ("Id", "FriendlyName", "ZoneReferenceText", "ResolvedMapZoneId", "SortOrder", "CreatedUtc", "UpdatedUtc")
            SELECT "Id", "FriendlyName", "ZoneReferenceText", "ResolvedMapZoneId", "SortOrder", "CreatedUtc", "UpdatedUtc"
            FROM "RoomGroups";
            """);

        migrationBuilder.DropTable(name: "RoomGroups");
        migrationBuilder.RenameTable(name: "__RoomGroups_without_virtual", newName: "RoomGroups");
        migrationBuilder.CreateIndex(
            name: "IX_RoomGroups_ResolvedMapZoneId",
            table: "RoomGroups",
            column: "ResolvedMapZoneId");

        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
}
