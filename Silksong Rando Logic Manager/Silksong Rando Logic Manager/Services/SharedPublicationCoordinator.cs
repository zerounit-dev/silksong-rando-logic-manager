using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Process-wide application publication admission, committed completion
/// inputs and identity-only cache history. Legacy scope brackets are receipt provenance.</summary>
public sealed class SharedPublicationCoordinator
{
    public sealed record PreparedPublication(SharedFreshness After, SharedMutationTimestamps Timestamps);
    private readonly SemaphoreSlim gate = new(1, 1);
    private SharedFreshness current = new(DateTime.UtcNow, DateTime.UtcNow);
    private LogicCacheHistory? cache;
    private RecordHistoryStore? records;
    private bool standalone;

    // Explicit operator composition, never inferred from absent web initialization.
    internal static SharedPublicationCoordinator CreateStandalone() => new() { standalone = true };

    public async Task<Admission> AcquireAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        return new(this);
    }

    internal sealed record GroupWriteFact(Guid Id, string FriendlyName, int SortOrder, string? ZoneReferenceText,
        Guid? ResolvedMapZoneId, bool IsVirtual, DateTime CreatedUtc, DateTime UpdatedUtc);
    public sealed class Admission : IDisposable
    {
        private SharedPublicationCoordinator? owner;
        private SharedFreshness segmentBefore;
        internal Admission(SharedPublicationCoordinator owner)
        {
            this.owner = owner;
            segmentBefore = owner.current;
        }
        public SharedFreshness Tokens => Owner.current;
        internal LogicCacheHistory Cache => Owner.cache
            ?? throw new InvalidOperationException("Logic cache history has not been initialized after migrations/completion backfill.");

        internal async Task InitializeRecordHistoryAsync(RecordHistoryStore store, Data.LogicDbContext db,
            LogicCacheSql.SchemaManifest schema)
        {
            if (Owner.standalone || Owner.records is not null) throw new InvalidOperationException("Invalid record history composition.");
            await store.InitializeAsync(db, schema);
            Owner.records = store;
        }

        internal RecordHistoryStore? RequireRecordHistory(HistoryActor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (Owner.standalone != actor.Standalone) throw new InvalidOperationException("History actor and execution composition disagree.");
            if (Owner.standalone) return null;
            return Owner.records is { Initialized: true } store ? store
                : throw new InvalidOperationException("Web record history must be initialized before logic writes.");
        }

        // Normal startup only, after migrations and before serving. The caller owns
        // the initialization transaction; publish no history until its backfill commits.
        internal void InitializeCache(LogicCacheSql.SchemaManifest schema, AppliedCompletionIndex completion)
        {
            if (Owner.cache is not null) throw new InvalidOperationException("Logic cache history already initialized.");
            Owner.cache = new(schema, completion);
        }
        private SharedPublicationCoordinator Owner => owner ?? throw new ObjectDisposedException(nameof(Admission));

        // Reserve while still before commit: overflow or clock acquisition must
        // never interpose between an actual commit and its reliable receipt.
        public PreparedPublication PrepareAdvance(bool rooms, bool requirements)
        {
            var old = Tokens;
            var now = DateTime.UtcNow;
            var after = new SharedFreshness(rooms ? Advance(old.RoomsUtc, now) : old.RoomsUtc,
                requirements ? Advance(old.RequirementsUtc, now) : old.RequirementsUtc);
            return new(after, new(new(segmentBefore.RoomsUtc, after.RoomsUtc),
                new(segmentBefore.RequirementsUtc, after.RequirementsUtc)));
        }

        // All return evidence is allocated before commit. This assignment belongs
        // immediately AFTER an existing reliable receipt, never ahead of it.
        public SharedMutationTimestamps PublishCommitted(PreparedPublication prepared)
        {
            Owner.current = prepared.After;
            segmentBefore = prepared.After;
            return prepared.Timestamps;
        }

        private static DateTime Advance(DateTime previous, DateTime now)
        {
            if (previous.Ticks == DateTime.MaxValue.Ticks) throw new OverflowException("Shared freshness timestamp exhausted.");
            return new(Math.Max(now.Ticks, previous.Ticks + 1), DateTimeKind.Utc);
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.gate.Release();
    }
}
