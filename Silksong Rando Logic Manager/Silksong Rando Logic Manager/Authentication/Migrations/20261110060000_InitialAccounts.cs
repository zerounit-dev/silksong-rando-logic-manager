using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Silksong_Rando_Logic_Manager.Authentication.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261110060000_InitialAccounts")]
public sealed class InitialAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("AspNetUsers", table => new
        {
            Id = table.Column<Guid>("TEXT", nullable: false),
            Role = table.Column<string>("TEXT", nullable: false),
            Enabled = table.Column<bool>("INTEGER", nullable: false),
            MustReplacePassword = table.Column<bool>("INTEGER", nullable: false),
            UserName = table.Column<string>("TEXT", maxLength: 64, nullable: true),
            NormalizedUserName = table.Column<string>("TEXT", maxLength: 64, nullable: true),
            Email = table.Column<string>("TEXT", maxLength: 256, nullable: true),
            NormalizedEmail = table.Column<string>("TEXT", maxLength: 256, nullable: true),
            EmailConfirmed = table.Column<bool>("INTEGER", nullable: false),
            PasswordHash = table.Column<string>("TEXT", nullable: true),
            SecurityStamp = table.Column<string>("TEXT", nullable: true),
            ConcurrencyStamp = table.Column<string>("TEXT", nullable: true),
            PhoneNumber = table.Column<string>("TEXT", nullable: true),
            PhoneNumberConfirmed = table.Column<bool>("INTEGER", nullable: false),
            TwoFactorEnabled = table.Column<bool>("INTEGER", nullable: false),
            LockoutEnd = table.Column<DateTimeOffset>("TEXT", nullable: true),
            LockoutEnabled = table.Column<bool>("INTEGER", nullable: false),
            AccessFailedCount = table.Column<int>("INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_AspNetUsers", x => x.Id));
        migrationBuilder.CreateTable("AspNetUserClaims", table => new
        {
            Id = table.Column<int>("INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            UserId = table.Column<Guid>("TEXT", nullable: false),
            ClaimType = table.Column<string>("TEXT", nullable: true),
            ClaimValue = table.Column<string>("TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
            table.ForeignKey("FK_AspNetUserClaims_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateTable("AspNetUserLogins", table => new
        {
            LoginProvider = table.Column<string>("TEXT", nullable: false),
            ProviderKey = table.Column<string>("TEXT", nullable: false),
            ProviderDisplayName = table.Column<string>("TEXT", nullable: true),
            UserId = table.Column<Guid>("TEXT", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
            table.ForeignKey("FK_AspNetUserLogins_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateTable("AspNetUserTokens", table => new
        {
            UserId = table.Column<Guid>("TEXT", nullable: false),
            LoginProvider = table.Column<string>("TEXT", nullable: false),
            Name = table.Column<string>("TEXT", nullable: false),
            Value = table.Column<string>("TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
            table.ForeignKey("FK_AspNetUserTokens_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
        });
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
        migrationBuilder.CreateIndex("EmailIndex", "AspNetUsers", "NormalizedEmail");
        migrationBuilder.CreateIndex("UserNameIndex", "AspNetUsers", "NormalizedUserName", unique: true);
        migrationBuilder.CreateIndex("IX_AspNetUserClaims_UserId", "AspNetUserClaims", "UserId");
        migrationBuilder.CreateIndex("IX_AspNetUserLogins_UserId", "AspNetUserLogins", "UserId");
        migrationBuilder.CreateIndex("IX_Sessions_AccountId", "Sessions", "AccountId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Sessions");
        migrationBuilder.DropTable("AspNetUserClaims");
        migrationBuilder.DropTable("AspNetUserLogins");
        migrationBuilder.DropTable("AspNetUserTokens");
        migrationBuilder.DropTable("AspNetUsers");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AccountsModelV1.Build(modelBuilder);
}

// Frozen initial account model, shared only by this migration's target and snapshot.
// Never call this from the live context or edit it when adding later migrations.
internal static class AccountsModelV1
{
    public static void Build(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.10");
        var account = model.Entity("Silksong_Rando_Logic_Manager.Authentication.Account");
        account.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
        foreach (var name in new[] { "AccessFailedCount" }) account.Property<int>(name).HasColumnType("INTEGER");
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
        var sessions = model.Entity("Silksong_Rando_Logic_Manager.Authentication.AccountSession");
        sessions.Property<string>("Digest").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
        sessions.Property<Guid>("AccountId").HasColumnType("TEXT"); sessions.Property<string>("Generation").IsRequired().HasColumnType("TEXT");
        sessions.Property<DateTime>("IssuedUtc").HasColumnType("TEXT"); sessions.HasKey("Digest"); sessions.HasIndex("AccountId"); sessions.ToTable("Sessions");
        sessions.HasOne(account.Metadata.Name, null).WithMany().HasForeignKey("AccountId").OnDelete(DeleteBehavior.Cascade).IsRequired();
    }
}
