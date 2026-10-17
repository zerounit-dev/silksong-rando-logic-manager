using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20261015120000_AddRoomImageIdentities")]
public sealed class AddRoomImageIdentities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("UseUploadedSceneImage", "Rooms", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>("SceneImageOriginalHash", "Rooms", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("SceneImageCropHash", "Rooms", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Room image identities are patch-forward only; downgrade is unsupported.");
}
