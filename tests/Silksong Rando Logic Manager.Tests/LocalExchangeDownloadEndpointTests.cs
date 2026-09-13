using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class LocalExchangeDownloadEndpointTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-export-endpoints-{Guid.NewGuid():N}.db");
    private readonly Guid roomId = Guid.NewGuid();
    private static readonly DateTime Stamp = new(2026, 8, 16, 14, 30, 45, 123, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        db.Add(new Room { Id = roomId, FriendlyName = "Endpoint room", ReferenceId = "endpoint-ref" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteDownload_UsesTypedServiceResultAsUtf8JsonAttachment(bool includeAreaMap)
    {
        var expected = await Service().ExportCompleteAsync(includeAreaMap);
        var response = await ExecuteAsync(await LocalExchangeDownloadEndpoints.DownloadCompleteAsync(includeAreaMap, Service()));

        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal($"attachment; filename={expected.FileName}; filename*=UTF-8''{expected.FileName}", response.Headers.ContentDisposition);
        Assert.Equal(expected.JsonUtf8, response.Body);
        Assert.Equal(includeAreaMap, System.Text.Json.JsonDocument.Parse(response.Body).RootElement.TryGetProperty("areaMap", out _));
    }

    [Fact]
    public async Task CurrentRoomDownload_UsesExactFilenameAndMissingRoomIsNotFound()
    {
        var expected = Assert.IsType<CurrentRoomExported>(await Service().ExportCurrentRoomAsync(roomId)).Export;
        var current = await LocalExchangeDownloadEndpoints.DownloadCurrentRoomAsync(roomId, Service());
        var response = await ExecuteAsync(current);

        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal($"attachment; filename={expected.FileName}; filename*=UTF-8''{expected.FileName}", response.Headers.ContentDisposition);
        Assert.Equal(expected.JsonUtf8, response.Body);

        var missing = await LocalExchangeDownloadEndpoints.DownloadCurrentRoomAsync(Guid.NewGuid(), Service());
        var missingResponse = await ExecuteAsync(missing);
        Assert.Equal(StatusCodes.Status404NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public void EndpointBoundary_UsesOnlyTypedExportServiceAndNoEfContract()
    {
        var methods = typeof(LocalExchangeDownloadEndpoints).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.Contains(methods, x => x.Name == nameof(LocalExchangeDownloadEndpoints.DownloadCompleteAsync) && x.GetParameters().Any(p => p.ParameterType == typeof(DistributedExportService)));
        Assert.Contains(methods, x => x.Name == nameof(LocalExchangeDownloadEndpoints.DownloadCurrentRoomAsync) && x.GetParameters().Any(p => p.ParameterType == typeof(DistributedExportService)));
        Assert.DoesNotContain(methods.SelectMany(x => x.GetParameters()), x => typeof(DbContext).IsAssignableFrom(x.ParameterType) || x.ParameterType == typeof(LogicDbContext));
    }

    private async Task<(int StatusCode, string? ContentType, IHeaderDictionary Headers, byte[] Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.ContentType, context.Response.Headers, body.ToArray());
    }

    private DistributedExportService Service() => new(new Factory(databasePath), new FixedTimeProvider(Stamp));
    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
    private sealed class Factory(string path) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
}
