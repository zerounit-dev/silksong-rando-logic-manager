using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20260807011851_AddSceneLayoutPersistence")]
public sealed class AddSceneLayoutPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(name: "SceneUnitHeight", table: "Subrooms", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<double>(name: "SceneUnitWidth", table: "Subrooms", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<double>(name: "SceneUnitX", table: "Subrooms", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<double>(name: "SceneUnitY", table: "Subrooms", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "EnableAnnotation", table: "SubroomConnections", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<double>(name: "SceneUnitX", table: "SubroomConnections", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<double>(name: "SceneUnitY", table: "SubroomConnections", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<double>(name: "SceneUnitHeight", table: "Rooms", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<double>(name: "SceneUnitWidth", table: "Rooms", type: "REAL", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "EnableAnnotation", table: "CheckLocations", type: "INTEGER", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SceneUnitHeight", table: "Subrooms");
        migrationBuilder.DropColumn(name: "SceneUnitWidth", table: "Subrooms");
        migrationBuilder.DropColumn(name: "SceneUnitX", table: "Subrooms");
        migrationBuilder.DropColumn(name: "SceneUnitY", table: "Subrooms");
        migrationBuilder.DropColumn(name: "EnableAnnotation", table: "SubroomConnections");
        migrationBuilder.DropColumn(name: "SceneUnitX", table: "SubroomConnections");
        migrationBuilder.DropColumn(name: "SceneUnitY", table: "SubroomConnections");
        migrationBuilder.DropColumn(name: "SceneUnitHeight", table: "Rooms");
        migrationBuilder.DropColumn(name: "SceneUnitWidth", table: "Rooms");
        migrationBuilder.DropColumn(name: "EnableAnnotation", table: "CheckLocations");
    }
}
