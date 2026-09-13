using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class DefaultMapImageCalibrationValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "MapOffsetYPercent",
                table: "MapImageCalibrations",
                type: "REAL",
                nullable: true,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.Sql("UPDATE \"MapImageCalibrations\" SET \"MapHeightScalePercent\" = 100.0 WHERE \"MapHeightScalePercent\" IS NULL; UPDATE \"MapImageCalibrations\" SET \"MapOffsetXPercent\" = 0.0 WHERE \"MapOffsetXPercent\" IS NULL; UPDATE \"MapImageCalibrations\" SET \"MapOffsetYPercent\" = 0.0 WHERE \"MapOffsetYPercent\" IS NULL;");

            migrationBuilder.AlterColumn<double>(
                name: "MapOffsetXPercent",
                table: "MapImageCalibrations",
                type: "REAL",
                nullable: true,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "MapHeightScalePercent",
                table: "MapImageCalibrations",
                type: "REAL",
                nullable: true,
                defaultValue: 100.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "MapOffsetYPercent",
                table: "MapImageCalibrations",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 0.0);

            migrationBuilder.AlterColumn<double>(
                name: "MapOffsetXPercent",
                table: "MapImageCalibrations",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 0.0);

            migrationBuilder.AlterColumn<double>(
                name: "MapHeightScalePercent",
                table: "MapImageCalibrations",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true,
                oldDefaultValue: 100.0);
        }
    }
}
