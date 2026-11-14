using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomAudit;

/// <summary>Permanent execution and I/O boundary for the two coexisting audit hosts.</summary>
public abstract class AuditHost
{
    public virtual bool Blocked => false;
    public virtual bool ReloadAvailable => false;
    public virtual bool PerformanceLogging => false;
    public virtual Microsoft.AspNetCore.Components.ElementReference ExternalModal => default;
    public abstract Task<RoomAuditSnapshot?> LoadAsync(Guid roomId, Func<bool> owns, CancellationToken token);
    public abstract Task<AuditRoomCatalogue> LoadCatalogueAsync(CancellationToken token);
    public abstract string RoomUrl(Guid roomId);
    public abstract string AuditUrl(Guid roomId);
    public abstract string BackLabel { get; }
    public abstract Task<T> ExecuteAsync<T>(Func<T> work, CancellationToken token);
}
