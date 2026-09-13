using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Silksong_Rando_Logic_Manager.Data;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
[Migration("20260905160000_AddRequirementValidationStatus")]
public partial class AddRequirementValidationStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "RequirementsParseSucceeded", table: "RoomTransitions", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "RequirementsParseSucceeded", table: "SubroomConnections", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "RequirementsParseSucceeded", table: "CheckLocations", type: "INTEGER", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RequirementsParseSucceeded", table: "RoomTransitions");
        migrationBuilder.DropColumn(name: "RequirementsParseSucceeded", table: "SubroomConnections");
        migrationBuilder.DropColumn(name: "RequirementsParseSucceeded", table: "CheckLocations");
    }
}
