using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Silksong_Rando_Logic_Manager.Authentication.Migrations;

[DbContext(typeof(AccountsDbContext))]
public sealed class AccountsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => PermissionsAccountsModel.Build(modelBuilder);
}
