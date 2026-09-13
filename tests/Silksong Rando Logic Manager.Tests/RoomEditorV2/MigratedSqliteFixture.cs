using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Owns one migration-current SQLite database outside the application data path.</summary>
public sealed class MigratedSqliteFixture : IAsyncDisposable, IDbContextFactory<LogicDbContext>
{
    private readonly string directoryPath;
    private readonly IInterceptor? interceptor;
    private bool disposed;

    private MigratedSqliteFixture(string directoryPath, IInterceptor? interceptor)
    {
        this.directoryPath = directoryPath;
        this.interceptor = interceptor;
        DatabasePath = Path.Combine(directoryPath, "logic.db");
    }

    public string DatabasePath { get; }

    public static async Task<MigratedSqliteFixture> CreateAsync(CancellationToken cancellationToken = default, IInterceptor? interceptor = null)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "silksong-rando-logic-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var fixture = new MigratedSqliteFixture(directoryPath, interceptor);

        try
        {
            await using var db = fixture.CreateDbContext();
            await db.Database.MigrateAsync(cancellationToken);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public LogicDbContext CreateDbContext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={DatabasePath}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LogicDbContext(options.Options);
    }

    public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
