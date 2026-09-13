using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SidebarWorkspaceProjectionTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-sidebar-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetWorkspaceAsync_ProjectsOnlyOrderedSidebarTreeAndTodoState()
    {
        Guid todoRoomId;
        await using (var db = CreateContext())
        {
            var group = new RoomGroup { FriendlyName = "Second", SortOrder = 1 };
            var firstGroup = new RoomGroup { FriendlyName = "First", SortOrder = 0 };
            var todoRoom = new Room { RoomGroup = group, FriendlyName = "Todo", ReferenceId = "todo", SortOrder = 1 };
            var archivedRoom = new Room { RoomGroup = group, FriendlyName = "Archived", ReferenceId = "archived", SortOrder = 0, IsArchived = true };
            db.AddRange(group, firstGroup, todoRoom, archivedRoom);
            await db.SaveChangesAsync();
            db.RoomTransitions.Add(new RoomTransition { RoomId = todoRoom.Id, FriendlyName = "Exit", Alias = "E", IsTodo = true, SortOrder = 0 });
            todoRoomId = todoRoom.Id;
            await db.SaveChangesAsync();
        }

        var commands = new CommandCaptureInterceptor();
        var catalog = new LogicCatalogService(new Factory(databasePath, commands));
        var snapshot = await catalog.GetWorkspaceAsync();

        Assert.Equal(["First", "Second"], snapshot.RoomGroups.Select(x => x.FriendlyName));
        Assert.Equal(["Archived", "Todo"], snapshot.Rooms.Select(x => x.FriendlyName));
        Assert.Equal(AppliedRoomStatus.Warning, snapshot.Rooms.Single(x => x.Id == todoRoomId).Status);
        Assert.DoesNotContain(commands.Commands, command => command.Contains("MapZones", StringComparison.OrdinalIgnoreCase) || command.Contains("MapScenes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands.Commands, command => command.Contains("SELECT \"r\".\"ReferenceId\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetGroupEditorDataAsync_LoadsSelectedDraftSuggestionsAndCurrentStatusOnDemand()
    {
        Guid groupId;
        Guid zoneId;
        await using (var db = CreateContext())
        {
            var map = new Map { InGameId = "map", SortOrder = 0 };
            var zone = new MapZone { Map = map, InGameId = "BONE" };
            var group = new RoomGroup { FriendlyName = "Bone", ZoneReferenceText = " bone ", ResolvedMapZone = zone, SortOrder = 0 };
            db.AddRange(map, zone, group);
            await db.SaveChangesAsync();
            groupId = group.Id;
            zoneId = zone.Id;
        }

        var catalog = new LogicCatalogService(new Factory(databasePath));
        var editor = await catalog.GetGroupEditorDataAsync(groupId);

        Assert.NotNull(editor);
        Assert.Equal(groupId, editor.Group.Id);
        Assert.Equal("Bone", editor.Group.FriendlyName);
        Assert.Equal(" bone ", editor.Group.ZoneReferenceText);
        Assert.Equal(zoneId, editor.Group.ResolvedMapZoneId);
        Assert.Equal(["BONE"], editor.ZoneIds);
        Assert.Equal(ReferenceResolutionStatus.Resolved, editor.ZoneReferenceStatus);
    }

    [Fact]
    public async Task GetGroupEditorDataAsync_HidesStatusForBlankAuthoredReference()
    {
        Guid groupId;
        await using (var db = CreateContext())
        {
            var group = new RoomGroup { FriendlyName = "Blank", SortOrder = 0 };
            db.RoomGroups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
        }

        var editor = await new LogicCatalogService(new Factory(databasePath)).GetGroupEditorDataAsync(groupId);

        Assert.NotNull(editor);
        Assert.Null(editor.ZoneReferenceStatus);
    }

    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);

    private sealed class Factory(string path, CommandCaptureInterceptor? commands = null) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create()
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}");
            if (commands is not null) options.AddInterceptors(commands);
            return new LogicDbContext(options.Options);
        }
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
