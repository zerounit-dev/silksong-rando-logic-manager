using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Silksong_Rando_Logic_Manager.Data;

public sealed class LogicDbContext(DbContextOptions<LogicDbContext> options) : DbContext(options)
{
    private int suppressAuditMetadata;
    public DbSet<RoomGroup> RoomGroups => Set<RoomGroup>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Subroom> Subrooms => Set<Subroom>();
    public DbSet<RoomTransition> RoomTransitions => Set<RoomTransition>();
    public DbSet<SubroomConnection> SubroomConnections => Set<SubroomConnection>();
    public DbSet<CheckLocation> CheckLocations => Set<CheckLocation>();
    public DbSet<Map> Maps => Set<Map>();
    public DbSet<MapZone> MapZones => Set<MapZone>();
    public DbSet<MapScene> MapScenes => Set<MapScene>();
    public DbSet<MapChunk> MapChunks => Set<MapChunk>();
    public DbSet<MapOverlay> MapOverlays => Set<MapOverlay>();
    public DbSet<RequirementPredicate> RequirementPredicates => Set<RequirementPredicate>();
    public DbSet<RequirementItem> RequirementItems => Set<RequirementItem>();

#if DEBUG
    public override void Dispose()
    {
        try
        {
            base.Dispose();
        }
        finally
        {
            Services.DebugLogicDbContextLifecycle.Disposed(this);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            Services.DebugLogicDbContextLifecycle.Disposed(this);
        }
    }
#endif

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureRoomGroup(modelBuilder.Entity<RoomGroup>());
        ConfigureRoom(modelBuilder.Entity<Room>());
        ConfigureSubroom(modelBuilder.Entity<Subroom>());
        ConfigureRoomTransition(modelBuilder.Entity<RoomTransition>());
        ConfigureSubroomConnection(modelBuilder.Entity<SubroomConnection>());
        ConfigureCheckLocation(modelBuilder.Entity<CheckLocation>());
        ConfigureMap(modelBuilder.Entity<Map>());
        ConfigureMapZone(modelBuilder.Entity<MapZone>());
        ConfigureMapScene(modelBuilder.Entity<MapScene>());
        ConfigureMapChunk(modelBuilder.Entity<MapChunk>());
        ConfigureMapOverlay(modelBuilder.Entity<MapOverlay>());
        ConfigureRequirementPredicate(modelBuilder.Entity<RequirementPredicate>());
        ConfigureRequirementItem(modelBuilder.Entity<RequirementItem>());
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditMetadata();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditMetadata();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Exchange documents own their supplied audit values. This narrowly scoped
    // switch is used only while an exchange transaction writes those documents
    // and its resolver-owned IDs.
    public IDisposable SuppressAuditMetadata()
    {
        suppressAuditMetadata++;
        return new AuditSuppression(this);
    }

    private static void ConfigureRoomGroup(EntityTypeBuilder<RoomGroup> entity)
    {
        ConfigureAudited(entity);
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.ZoneReferenceText).UseCollation("NOCASE");
        entity.HasOne(x => x.ResolvedMapZone).WithMany().HasForeignKey(x => x.ResolvedMapZoneId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.Rooms).WithOne(x => x.RoomGroup).HasForeignKey(x => x.RoomGroupId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRoom(EntityTypeBuilder<Room> entity)
    {
        ConfigureArchivable(entity);
        entity.Property(x => x.ReferenceId).IsRequired().UseCollation("NOCASE");
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.InGameId).UseCollation("NOCASE");
        entity.Property(x => x.IsSceneImageStale).HasDefaultValue(false);
        entity.HasMany(x => x.Subrooms).WithOne(x => x.Room).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.Transitions).WithOne(x => x.Room).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.Connections).WithOne(x => x.Room).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.CheckLocations).WithOne(x => x.Room).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSubroom(EntityTypeBuilder<Subroom> entity)
    {
        ConfigureArchivable(entity);
        entity.Property(x => x.ReferenceId).IsRequired().UseCollation("NOCASE");
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.EnableAnnotation).HasDefaultValue(true);
    }

    private static void ConfigureRoomTransition(EntityTypeBuilder<RoomTransition> entity)
    {
        ConfigureArchivable(entity);
        entity.Property(x => x.Alias).IsRequired();
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.InGameId).UseCollation("NOCASE");
        entity.Property(x => x.Requirements).IsRequired();
        entity.Property(x => x.RequirementsParseSucceeded).HasDefaultValue(null);
        entity.Property(x => x.Notes).IsRequired();
        entity.Property(x => x.IsVerified).HasDefaultValue(null);
        entity.Property(x => x.EnableAnnotation).HasDefaultValue(true);
        entity.HasOne(x => x.ResolvedSourceSubroom).WithMany().HasForeignKey(x => x.ResolvedSourceSubroomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ResolvedDestinationRoom).WithMany().HasForeignKey(x => x.ResolvedDestinationRoomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ResolvedDestinationTransition).WithMany().HasForeignKey(x => x.ResolvedDestinationTransitionId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSubroomConnection(EntityTypeBuilder<SubroomConnection> entity)
    {
        ConfigureArchivable(entity);
        entity.Property(x => x.Alias).IsRequired();
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.SourceSubroomReferenceText).IsRequired();
        entity.Property(x => x.DestinationSubroomReferenceText).IsRequired();
        entity.Property(x => x.Requirements).IsRequired();
        entity.Property(x => x.RequirementsParseSucceeded).HasDefaultValue(null);
        entity.Property(x => x.Notes).IsRequired();
        entity.Property(x => x.IsVerified).HasDefaultValue(null);
        entity.Property(x => x.EnableAnnotation).HasDefaultValue(true);
        entity.HasOne(x => x.ResolvedSourceSubroom).WithMany().HasForeignKey(x => x.ResolvedSourceSubroomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ResolvedDestinationSubroom).WithMany().HasForeignKey(x => x.ResolvedDestinationSubroomId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCheckLocation(EntityTypeBuilder<CheckLocation> entity)
    {
        ConfigureArchivable(entity);
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.InGameId).UseCollation("NOCASE");
        entity.Property(x => x.Requirements).IsRequired();
        entity.Property(x => x.RequirementsParseSucceeded).HasDefaultValue(null);
        entity.Property(x => x.Notes).IsRequired();
        entity.Property(x => x.LocationType);
        entity.Property(x => x.IsVerified).HasDefaultValue(null);
        entity.Property(x => x.EnableAnnotation).HasDefaultValue(true);
        entity.HasOne(x => x.ResolvedSubroom).WithMany().HasForeignKey(x => x.ResolvedSubroomId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMap(EntityTypeBuilder<Map> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.InGameId).IsRequired().UseCollation("NOCASE");
        entity.HasIndex(x => x.InGameId).IsUnique();
    }

    private static void ConfigureMapZone(EntityTypeBuilder<MapZone> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.InGameId).IsRequired().UseCollation("NOCASE");
        entity.HasOne(x => x.Map).WithMany(x => x.Zones).HasForeignKey(x => x.MapId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.MapId, x.InGameId }).IsUnique();
    }

    private static void ConfigureMapScene(EntityTypeBuilder<MapScene> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.InGameId).IsRequired().UseCollation("NOCASE");
        entity.HasOne(x => x.MapZone).WithMany(x => x.Scenes).HasForeignKey(x => x.MapZoneId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ResolvedRoom).WithMany().HasForeignKey(x => x.ResolvedRoomId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.MapZoneId, x.InGameId }).IsUnique();
    }

    private static void ConfigureMapChunk(EntityTypeBuilder<MapChunk> entity)
    {
        entity.HasKey(x => x.Id);
        entity.HasOne(x => x.MapScene).WithMany(x => x.Chunks).HasForeignKey(x => x.MapSceneId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.MapSceneId, x.CacheIndex }).IsUnique();
    }

    private static void ConfigureMapOverlay(EntityTypeBuilder<MapOverlay> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.FriendlyName).IsRequired();
        entity.Property(x => x.ImageAssetKey).IsRequired();
        entity.Property(x => x.ScaleXPercent).HasDefaultValue(100d);
        entity.Property(x => x.ScaleYPercent).HasDefaultValue(100d);
        entity.Property(x => x.LeftOffsetPercent).HasDefaultValue(0d);
        entity.Property(x => x.BottomOffsetPercent).HasDefaultValue(0d);
        entity.HasOne(x => x.Map).WithMany(x => x.Overlays).HasForeignKey(x => x.MapId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRequirementPredicate(EntityTypeBuilder<RequirementPredicate> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired();
        entity.Property(x => x.InputSyntax).IsRequired();
        entity.Property(x => x.OutputSyntax).IsRequired();
        entity.Property(x => x.Aliases).IsRequired();
        entity.Property(x => x.Notes).IsRequired();
    }

    private static void ConfigureRequirementItem(EntityTypeBuilder<RequirementItem> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired();
        entity.Property(x => x.OutputValue).IsRequired();
        entity.Property(x => x.Aliases).IsRequired();
        entity.Property(x => x.Notes).IsRequired();
    }

    private static void ConfigureArchivable<TEntity>(EntityTypeBuilder<TEntity> entity) where TEntity : ArchivableEntity
    {
        ConfigureAudited(entity);
        entity.Property(x => x.IsArchived).HasDefaultValue(false);
    }

    private static void ConfigureAudited<TEntity>(EntityTypeBuilder<TEntity> entity) where TEntity : AuditedEntity
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.CreatedUtc).IsRequired();
        entity.Property(x => x.UpdatedUtc).IsRequired();
    }

    private void ApplyAuditMetadata()
    {
        if (suppressAuditMetadata != 0) return;
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedUtc = now;
                entry.Entity.UpdatedUtc = now;
                if (entry.Entity is ArchivableEntity archivable)
                {
                    archivable.ArchivedUtc = archivable.IsArchived ? now : null;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedUtc = now;
                if (entry.Entity is ArchivableEntity archivable)
                {
                    if (archivable.IsArchived && !entry.OriginalValues.GetValue<bool>(nameof(ArchivableEntity.IsArchived)))
                    {
                        archivable.ArchivedUtc = now;
                    }
                    else if (!archivable.IsArchived)
                    {
                        archivable.ArchivedUtc = null;
                    }
                }
            }
        }
    }

    private sealed class AuditSuppression(LogicDbContext owner) : IDisposable
    {
        public void Dispose() => owner.suppressAuditMetadata--;
    }
}
