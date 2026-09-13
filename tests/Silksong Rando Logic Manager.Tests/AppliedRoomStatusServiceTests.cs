using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class AppliedRoomStatusServiceTests
{
    private static readonly DateTime Utc = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LoadAsync_ProjectsExactPrecedenceAndCrossRoomNameExceptionFromRealSqlite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var neutral = Room("neutral"); var success = Room("success"); var warning = Room("warning");
        var todo = Room("todo"); var danger = Room("danger"); var archived = Room("archived", true);
        var crossA = Room("cross-a"); var crossB = Room("cross-b");
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(neutral, success, warning, todo, danger, archived, crossA, crossB);
            db.AddRange(
                Check(success.Id, "success", true),
                Check(warning.Id, "warning", null),
                Check(todo.Id, "todo", true, todo: true),
                Check(danger.Id, "danger", true, locationType: null),
                Check(archived.Id, "archived danger", false, locationType: null),
                Check(crossA.Id, "shared check", true),
                Check(crossB.Id, "shared check", true));
            await db.SaveChangesAsync();
        }

        var statuses = await new AppliedRoomStatusService(fixture).LoadAsync();

        Assert.Equal(AppliedRoomStatus.Neutral, statuses[neutral.Id]);
        Assert.Equal(AppliedRoomStatus.Success, statuses[success.Id]);
        Assert.Equal(AppliedRoomStatus.Warning, statuses[warning.Id]);
        Assert.Equal(AppliedRoomStatus.Warning, statuses[todo.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, statuses[danger.Id]);
        Assert.Equal(AppliedRoomStatus.Neutral, statuses[archived.Id]);
        Assert.Equal(AppliedRoomStatus.Success, statuses[crossA.Id]);
        Assert.Equal(AppliedRoomStatus.Success, statuses[crossB.Id]);
    }

    [Fact]
    public async Task LoadAsync_UsesFiveSetBasedScalarReadersForAnyRequestedRoomCount()
    {
        var counter = new ReaderCounter();
        await using var fixture = await MigratedSqliteFixture.CreateAsync(interceptor: counter);
        var one = Room("one"); var two = Room("two");
        await using (var db = fixture.CreateDbContext()) { db.AddRange(one, two); await db.SaveChangesAsync(); }
        counter.ReaderCount = 0;
        var service = new AppliedRoomStatusService(fixture);

        var statuses = await service.LoadAsync([one.Id, two.Id]);

        Assert.Equal(2, statuses.Count);
        Assert.Equal(5, counter.ReaderCount);
        Assert.Equal(5, service.LastTrace!.SqlQueryCount);
        Assert.Equal(2, service.LastTrace.RoomFacts);
    }

    [Fact]
    public async Task LoadAsync_EmptyRequestExecutesNoSql()
    {
        var counter = new ReaderCounter();
        await using var fixture = await MigratedSqliteFixture.CreateAsync(interceptor: counter);
        counter.ReaderCount = 0;
        Assert.Empty(await new AppliedRoomStatusService(fixture).LoadAsync([]));
        Assert.Equal(0, counter.ReaderCount);
    }

    private static Room Room(string name, bool archived = false) => new()
    {
        Id = Guid.NewGuid(), ReferenceId = name, FriendlyName = name, IsArchived = archived,
        ArchivedUtc = archived ? Utc : null, CreatedUtc = Utc, UpdatedUtc = Utc
    };
    private static CheckLocation Check(Guid roomId, string name, bool? verified, bool todo = false, string? locationType = "collectible") => new()
    {
        Id = Guid.NewGuid(), RoomId = roomId, FriendlyName = name, Requirements = "logic",
        RequirementsParseSucceeded = true, LocationType = locationType, IsVerified = verified,
        IsTodo = todo, CreatedUtc = Utc, UpdatedUtc = Utc
    };

    private sealed class ReaderCounter : DbCommandInterceptor
    {
        public int ReaderCount { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCount++;
            return ValueTask.FromResult(result);
        }
    }
}
