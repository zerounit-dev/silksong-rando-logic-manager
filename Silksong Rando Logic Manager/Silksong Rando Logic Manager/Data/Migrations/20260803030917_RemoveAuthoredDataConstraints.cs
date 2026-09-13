using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Silksong_Rando_Logic_Manager.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAuthoredDataConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subrooms_RoomId_ReferenceId",
                table: "Subrooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SubroomConnections_Alias_Length",
                table: "SubroomConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomTransitions_Alias_Length",
                table: "RoomTransitions");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_InGameId",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_ReferenceId",
                table: "Rooms");

            migrationBuilder.CreateIndex(
                name: "IX_Subrooms_RoomId",
                table: "Subrooms",
                column: "RoomId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subrooms_RoomId",
                table: "Subrooms");

            migrationBuilder.CreateIndex(
                name: "IX_Subrooms_RoomId_ReferenceId",
                table: "Subrooms",
                columns: new[] { "RoomId", "ReferenceId" },
                unique: true,
                filter: "IsArchived = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SubroomConnections_Alias_Length",
                table: "SubroomConnections",
                sql: "length(Alias) BETWEEN 1 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomTransitions_Alias_Length",
                table: "RoomTransitions",
                sql: "length(Alias) BETWEEN 1 AND 3");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_InGameId",
                table: "Rooms",
                column: "InGameId",
                unique: true,
                filter: "IsArchived = 0 AND InGameId IS NOT NULL AND trim(InGameId) <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_ReferenceId",
                table: "Rooms",
                column: "ReferenceId",
                unique: true,
                filter: "IsArchived = 0");
        }
    }
}
