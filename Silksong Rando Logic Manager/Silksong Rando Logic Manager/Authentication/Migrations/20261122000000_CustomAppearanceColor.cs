using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Authentication.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261122000000_CustomAppearanceColor")]
public sealed class CustomAppearanceColor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("CustomAppearanceColor", "AspNetUsers", "TEXT", maxLength: 7, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("CustomAppearanceColor", "AspNetUsers");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AppearanceAccountsModel.Build(modelBuilder);
}

internal static class AppearanceAccountsModel
{
    public static void Build(ModelBuilder model)
    {
        PermissionsAccountsModel.Build(model);
        model.Entity("Silksong_Rando_Logic_Manager.Authentication.Account")
            .Property<string>("CustomAppearanceColor").HasMaxLength(7).HasColumnType("TEXT");
    }
}
