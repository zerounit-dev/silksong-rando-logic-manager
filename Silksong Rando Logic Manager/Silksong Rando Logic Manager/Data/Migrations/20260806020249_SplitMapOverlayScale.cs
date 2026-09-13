using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitMapOverlayScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ScalePercent",
                table: "MapOverlays",
                newName: "ScaleYPercent");

            migrationBuilder.AddColumn<double>(
                name: "ScaleXPercent",
                table: "MapOverlays",
                type: "REAL",
                nullable: false,
                defaultValue: 100.0);

            migrationBuilder.Sql("UPDATE \"MapOverlays\" SET \"ScaleXPercent\" = \"ScaleYPercent\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScaleXPercent",
                table: "MapOverlays");

            migrationBuilder.RenameColumn(
                name: "ScaleYPercent",
                table: "MapOverlays",
                newName: "ScalePercent");
        }
    }
}
