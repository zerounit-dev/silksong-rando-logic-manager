using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed class Account : IdentityUser<Guid>
{
    public bool ManageAdmins { get; set; }
    public bool ManageUsers { get; set; }
    public bool EditLogic { get; set; }
    public bool PublishDocumentation { get; set; }
    public bool Enabled { get; set; }
    public bool MustReplacePassword { get; set; }
    public string? CustomAppearanceColor { get; set; }
}

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : IdentityUserContext<Account, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Account>().Property(x => x.UserName).HasMaxLength(64);
        builder.Entity<Account>().Property(x => x.NormalizedUserName).HasMaxLength(64);
        builder.Entity<Account>().Property(x => x.CustomAppearanceColor).HasMaxLength(7);
    }
}
