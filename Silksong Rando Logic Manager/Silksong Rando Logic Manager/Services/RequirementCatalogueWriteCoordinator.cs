using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Serializes all in-process catalogue writes because catalogue rows deliberately
/// have no optimistic concurrency value. Callers validate and commit within the
/// admitted SQLite transaction.
/// </summary>
public sealed class RequirementCatalogueWriteCoordinator(IDbContextFactory<LogicDbContext> dbContextFactory)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<T> ExecuteAsync<T>(Func<LogicDbContext, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await operation(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }
}
