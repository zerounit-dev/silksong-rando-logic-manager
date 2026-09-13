using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceNeedsVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsVerification",
                table: "SubroomConnections");

            migrationBuilder.DropColumn(
                name: "NeedsVerification",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "NeedsVerification",
                table: "CheckLocations");

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "SubroomConnections",
                type: "INTEGER",
                nullable: true,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "RoomTransitions",
                type: "INTEGER",
                nullable: true,
                defaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsIncludedInApworld",
                table: "CheckLocations",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "CheckLocations",
                type: "INTEGER",
                nullable: true,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE \"SubroomConnections\" SET \"IsVerified\" = NULL;");
            migrationBuilder.Sql("UPDATE \"RoomTransitions\" SET \"IsVerified\" = NULL;");
            migrationBuilder.Sql("UPDATE \"CheckLocations\" SET \"IsVerified\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "SubroomConnections");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "RoomTransitions");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "CheckLocations");

            migrationBuilder.AddColumn<bool>(
                name: "NeedsVerification",
                table: "SubroomConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsVerification",
                table: "RoomTransitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsIncludedInApworld",
                table: "CheckLocations",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsVerification",
                table: "CheckLocations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
