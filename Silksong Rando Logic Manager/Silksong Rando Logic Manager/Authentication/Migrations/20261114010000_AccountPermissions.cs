using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Authentication.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261114010000_AccountPermissions")]
public sealed class AddAccountPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite CHECK reports this constraint name. Fail before touching account
        // values; EF's migration transaction retains the complete pre-cutover state.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "AuthPerm1SourceValidation" (
                "Supported" INTEGER NOT NULL,
                CONSTRAINT "AUTH-PERM1: unsupported source Role; restore matching private state and resolve the source role before retrying migration"
                    CHECK ("Supported" = 1));
            INSERT INTO "AuthPerm1SourceValidation" ("Supported")
                SELECT 0 WHERE EXISTS (SELECT 1 FROM "AspNetUsers" WHERE "Role" IS NULL OR "Role" NOT IN ('Admin', 'Contributor', 'ReadOnly'));
            DROP TABLE "AuthPerm1SourceValidation";
            """);
        foreach (var permission in new[] { "ManageAdmins", "ManageUsers", "EditLogic", "PublishDocumentation" })
            migrationBuilder.AddColumn<bool>(permission, "AspNetUsers", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.Sql("""
            UPDATE "AspNetUsers" SET
                "ManageAdmins" = CASE WHEN "Role" = 'Admin' THEN 1 ELSE 0 END,
                "ManageUsers" = CASE WHEN "Role" = 'Admin' THEN 1 ELSE 0 END,
                "EditLogic" = CASE WHEN "Role" IN ('Admin', 'Contributor') THEN 1 ELSE 0 END,
                "PublishDocumentation" = CASE WHEN "Role" = 'Admin' THEN 1 ELSE 0 END,
                "SecurityStamp" = lower(hex(randomblob(16))),
                "ConcurrencyStamp" = lower(hex(randomblob(16)));
            """);
        migrationBuilder.DropColumn("Role", "AspNetUsers");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "AUTH-PERM1 cannot losslessly downgrade Custom permissions to roles. Restore a consistent private pre-cutover account/key backup with matching binaries; image-only rollback is not state rollback.");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => PermissionsAccountsModel.Build(modelBuilder);
}

// Independent current target. Previous migration helpers remain frozen history.
internal static class PermissionsAccountsModel
{
    public static void Build(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.10");
        var account = model.Entity("Silksong_Rando_Logic_Manager.Authentication.Account");
        account.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
        account.Property<int>("AccessFailedCount").HasColumnType("INTEGER");
        foreach (var name in new[] { "ManageAdmins", "ManageUsers", "EditLogic", "PublishDocumentation", "Enabled", "MustReplacePassword", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled" }) account.Property<bool>(name).HasColumnType("INTEGER");
        foreach (var name in new[] { "UserName", "NormalizedUserName" }) account.Property<string>(name).HasMaxLength(64).HasColumnType("TEXT");
        foreach (var name in new[] { "Email", "NormalizedEmail" }) account.Property<string>(name).HasMaxLength(256).HasColumnType("TEXT");
        foreach (var name in new[] { "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumber" }) account.Property<string>(name).HasColumnType("TEXT");
        account.Property<string>("ConcurrencyStamp").IsConcurrencyToken();
        account.Property<DateTimeOffset?>("LockoutEnd").HasColumnType("TEXT");
        account.HasKey("Id"); account.HasIndex("NormalizedEmail").HasDatabaseName("EmailIndex");
        account.HasIndex("NormalizedUserName").IsUnique().HasDatabaseName("UserNameIndex"); account.ToTable("AspNetUsers");
        var claims = model.Entity("Microsoft.AspNetCore.Identity.IdentityUserClaim<System.Guid>");
        claims.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
        claims.Property<Guid>("UserId").HasColumnType("TEXT");
        claims.Property<string>("ClaimType").HasColumnType("TEXT"); claims.Property<string>("ClaimValue").HasColumnType("TEXT");
        claims.HasKey("Id"); claims.HasIndex("UserId"); claims.ToTable("AspNetUserClaims");
        claims.HasOne(account.Metadata.Name, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var logins = model.Entity("Microsoft.AspNetCore.Identity.IdentityUserLogin<System.Guid>");
        foreach (var name in new[] { "LoginProvider", "ProviderKey" }) logins.Property<string>(name).IsRequired().HasColumnType("TEXT");
        logins.Property<string>("ProviderDisplayName").HasColumnType("TEXT"); logins.Property<Guid>("UserId").HasColumnType("TEXT");
        logins.HasKey("LoginProvider", "ProviderKey"); logins.HasIndex("UserId"); logins.ToTable("AspNetUserLogins");
        logins.HasOne(account.Metadata.Name, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var tokens = model.Entity("Microsoft.AspNetCore.Identity.IdentityUserToken<System.Guid>");
        tokens.Property<Guid>("UserId").HasColumnType("TEXT");
        foreach (var name in new[] { "LoginProvider", "Name" }) tokens.Property<string>(name).IsRequired().HasColumnType("TEXT");
        tokens.Property<string>("Value").HasColumnType("TEXT"); tokens.HasKey("UserId", "LoginProvider", "Name"); tokens.ToTable("AspNetUserTokens");
        tokens.HasOne(account.Metadata.Name, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
    }
}
