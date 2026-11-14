using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Silksong_Rando_Logic_Manager.Data.Migrations;

[DbContext(typeof(LogicDbContext))]
partial class LogicDbContextModelSnapshot : ModelSnapshot
{
    // The target model is frozen in the self-contained migration designer.
    protected override void BuildModel(ModelBuilder modelBuilder) => AddRoomCompletionStatus.BuildFrozenModel(modelBuilder);
}
