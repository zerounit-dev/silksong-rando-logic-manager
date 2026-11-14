using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Authentication.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261114000000_RemoveSessions")]
public sealed class RemoveSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Sessions");

    // Recreating an empty legacy table cannot reconstruct old sign-ins. Operator
    // rollback uses matching private pre-cutover account state and binaries.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Sessions", table => new
        {
            Digest = table.Column<string>("TEXT", maxLength: 64, nullable: false),
            AccountId = table.Column<Guid>("TEXT", nullable: false),
            Generation = table.Column<string>("TEXT", nullable: false),
            IssuedUtc = table.Column<DateTime>("TEXT", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Sessions", x => x.Digest);
            table.ForeignKey("FK_Sessions_AspNetUsers_AccountId", x => x.AccountId, "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_Sessions_AccountId", "Sessions", "AccountId");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => NativeAccountsModel.Build(modelBuilder);
}

// Independent native target metadata; never delegates to the frozen V1 model or
// the live context. Account and Identity support tables are unchanged.
internal static class NativeAccountsModel
{
    public static void Build(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.10");
        var account = model.Entity("Silksong_Rando_Logic_Manager.Authentication.Account");
        account.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
        account.Property<int>("AccessFailedCount").HasColumnType("INTEGER");
        foreach (var name in new[] { "Enabled", "MustReplacePassword", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled" }) account.Property<bool>(name).HasColumnType("INTEGER");
        account.Property<string>("Role").IsRequired().HasColumnType("TEXT");
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
