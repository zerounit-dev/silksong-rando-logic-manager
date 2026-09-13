using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for the row-armed scene placement writes.</summary>
public sealed class RoomEditorV2AnnotationPlacementCommandTests
{
    [Fact]
    public async Task MigrationCurrentSqlite_DefaultsNewAnnotationRowsWithoutChangingExistingEnablement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-annotation-default-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options;
            var now = DateTime.UtcNow;
            var room = Guid.NewGuid();
            await using (var db = new LogicDbContext(options))
            {
                await db.Database.MigrateAsync("20260815170557_AddTransitionAnnotationVisibility");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Rooms (Id, FriendlyName, ReferenceId, SortOrder, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({room}, {"room"}, {"room"}, 0, 0, {now}, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RoomTransitions (Id, RoomId, Alias, FriendlyName, Requirements, Notes, SortOrder, IsTodo, IsArchived, EnableAnnotation, CreatedUtc, UpdatedUtc) VALUES ({Guid.NewGuid()}, {room}, {"a"}, {"exit"}, {"r"}, {""}, 0, 0, 0, 0, {now}, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckLocations (Id, RoomId, FriendlyName, Requirements, Notes, IsIncludedInApworld, SortOrder, IsTodo, IsArchived, EnableAnnotation, CreatedUtc, UpdatedUtc) VALUES ({Guid.NewGuid()}, {room}, {"check"}, {"r"}, {""}, 1, 0, 0, 0, 0, {now}, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SubroomConnections (Id, RoomId, Alias, FriendlyName, SourceSubroomReferenceText, DestinationSubroomReferenceText, Requirements, Notes, SortOrder, IsTodo, IsArchived, EnableAnnotation, CreatedUtc, UpdatedUtc) VALUES ({Guid.NewGuid()}, {room}, {"c"}, {"connection"}, {""}, {""}, {"r"}, {""}, 0, 0, 0, 0, {now}, {now})");
                await db.Database.MigrateAsync();
            }
            await using (var db = new LogicDbContext(options))
            {
                Assert.False((await db.RoomTransitions.SingleAsync()).EnableAnnotation);
                Assert.False((await db.CheckLocations.SingleAsync()).EnableAnnotation);
                Assert.False((await db.SubroomConnections.SingleAsync()).EnableAnnotation);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RoomTransitions (Id, RoomId, Alias, FriendlyName, Requirements, Notes, SortOrder, IsTodo, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({Guid.NewGuid()}, {room}, {"b"}, {"new exit"}, {"r"}, {""}, 1, 0, 0, {now}, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CheckLocations (Id, RoomId, FriendlyName, Requirements, Notes, IsIncludedInApworld, SortOrder, IsTodo, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({Guid.NewGuid()}, {room}, {"new check"}, {"r"}, {""}, 1, 1, 0, 0, {now}, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SubroomConnections (Id, RoomId, Alias, FriendlyName, SourceSubroomReferenceText, DestinationSubroomReferenceText, Requirements, Notes, SortOrder, IsTodo, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({Guid.NewGuid()}, {room}, {"d"}, {"new connection"}, {""}, {""}, {"r"}, {""}, 1, 0, 0, {now}, {now})");
                Assert.True((await db.RoomTransitions.SingleAsync(x => x.Alias == "b")).EnableAnnotation);
                Assert.True((await db.CheckLocations.SingleAsync(x => x.FriendlyName == "new check")).EnableAnnotation);
                Assert.True((await db.SubroomConnections.SingleAsync(x => x.Alias == "d")).EnableAnnotation);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task TransitionAnnotationVisibilityMigration_UsesTheCompleteLegacyEffectivePositionMatrix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-transition-annotation-matrix-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options;
            var now = DateTime.UtcNow;
            var boundaryOverride = Guid.NewGuid(); var boundaryGame = Guid.NewGuid(); var partialOverride = Guid.NewGuid(); var partialGameX = Guid.NewGuid(); var partialGameY = Guid.NewGuid(); var nonFinite = Guid.NewGuid(); var empty = Guid.NewGuid();
            await using (var legacy = new LogicDbContext(options))
            {
                await legacy.Database.MigrateAsync("20260814023457_AddRoomContributors");
                // This fixture intentionally models the schema immediately before
                // AddTransitionAnnotationVisibility, not the current EF model.
                await using (var columns = legacy.Database.GetDbConnection().CreateCommand())
                {
                    columns.CommandText = "SELECT name FROM pragma_table_info('RoomTransitions') WHERE name = 'EnableAnnotation';";
                    if (columns.Connection!.State != System.Data.ConnectionState.Open) await columns.Connection.OpenAsync();
                    await using var reader = await columns.ExecuteReaderAsync();
                    Assert.False(await reader.ReadAsync());
                }
                var room = Guid.NewGuid();
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Rooms (Id, FriendlyName, ReferenceId, SortOrder, IsArchived, CreatedUtc, UpdatedUtc) VALUES ({room}, {"room"}, {"room"}, 0, 0, {now}, {now})");
                foreach (var row in new (Guid Id, double? AnnotationX, double? AnnotationY, double? InGameX, double? InGameY)[]
                {
                    // Complete finite overrides take precedence even when game data is partial.
                    (boundaryOverride, (double?)double.MaxValue, (double?)-double.MaxValue, (double?)7, null),
                    // With no override, a complete finite imported pair is effective.
                    (boundaryGame, null, null, (double?)-double.MaxValue, (double?)double.MaxValue),
                    (partialOverride, (double?)1, null, (double?)7, (double?)8),
                    (partialGameX, null, null, (double?)7, null), (partialGameY, null, null, null, (double?)8),
                    (nonFinite, double.PositiveInfinity, (double?)2, (double?)7, (double?)8), (empty, null, null, null, null)
                })
                    await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RoomTransitions (Id, RoomId, Alias, FriendlyName, Requirements, Notes, SortOrder, IsTodo, IsArchived, CreatedUtc, UpdatedUtc, AnnotationSceneUnitX, AnnotationSceneUnitY, InGamePositionX, InGamePositionY) VALUES ({row.Id}, {room}, {"a"}, {"exit"}, {"r"}, {""}, 0, 0, 0, {now}, {now}, {row.AnnotationX}, {row.AnnotationY}, {row.InGameX}, {row.InGameY})");
                await legacy.Database.MigrateAsync();
            }
            await using var verify = new LogicDbContext(options);
            var enabled = await verify.RoomTransitions.Where(x => x.EnableAnnotation).Select(x => x.Id).ToListAsync();
            Assert.Equal(new[] { boundaryGame, boundaryOverride }.OrderBy(x => x), enabled.OrderBy(x => x));
            Assert.All(await verify.RoomTransitions.Where(x => x.Id == partialOverride || x.Id == partialGameX || x.Id == partialGameY || x.Id == nonFinite || x.Id == empty).ToListAsync(), row => Assert.False(row.EnableAnnotation));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Placement_WritesOnlyTransitionOverride_AndAtomicallyEnablesCheckWithPosition()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "authored exit", Requirements = "r", Notes = "note", InGameId = "game-exit", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "authored check", Requirements = "r", Notes = "note", InGameId = "game-check", InGamePositionX = 7, InGamePositionY = 8, InGamePositionZ = 9, LocalPositionX = 10, LocalPositionY = 11, LocalPositionZ = 12, EnableAnnotation = false };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition, check); await db.SaveChangesAsync(); }

        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture));
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.PlaceTransitionAnnotationAsync(room.Id, TransitionBaseline(transition), 20.25, 30.5)).Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.PlaceCheckAnnotationAsync(room.Id, CheckBaseline(check), 40.75, 50.125)).Status);

        await using var verify = fixture.CreateDbContext();
        var savedTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
        Assert.Equal((20.25d, 30.5d), (savedTransition.AnnotationSceneUnitX, savedTransition.AnnotationSceneUnitY));
        Assert.Equal(("a", "authored exit", "r", "note", "game-exit", 1d, 2d, 3d, 4d, 5d, 6d), (savedTransition.Alias, savedTransition.FriendlyName, savedTransition.Requirements, savedTransition.Notes, savedTransition.InGameId, savedTransition.InGamePositionX, savedTransition.InGamePositionY, savedTransition.InGamePositionZ, savedTransition.LocalPositionX, savedTransition.LocalPositionY, savedTransition.LocalPositionZ));
        var savedCheck = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
        Assert.True(savedCheck.EnableAnnotation);
        Assert.Equal((40.75d, 50.125d), (savedCheck.AnnotationSceneUnitX, savedCheck.AnnotationSceneUnitY));
        Assert.Equal(("authored check", "r", "note", "game-check", 7d, 8d, 9d, 10d, 11d, 12d), (savedCheck.FriendlyName, savedCheck.Requirements, savedCheck.Notes, savedCheck.InGameId, savedCheck.InGamePositionX, savedCheck.InGamePositionY, savedCheck.InGamePositionZ, savedCheck.LocalPositionX, savedCheck.LocalPositionY, savedCheck.LocalPositionZ));
    }

    [Fact]
    public async Task CancellationWithoutPlacementCommand_WritesNothing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r" };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition, check); await db.SaveChangesAsync(); }
        var transitionTime = transition.UpdatedUtc; var checkTime = check.UpdatedUtc;

        // Arming/cancelling is scene-browser transient state and deliberately has
        // no command-service entry point.
        await using var verify = fixture.CreateDbContext();
        var durableTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
        var durableCheck = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
        Assert.Equal(transitionTime, durableTransition.UpdatedUtc); Assert.Null(durableTransition.AnnotationSceneUnitX); Assert.Null(durableTransition.AnnotationSceneUnitY);
        Assert.Equal(checkTime, durableCheck.UpdatedUtc); Assert.True(durableCheck.EnableAnnotation); Assert.Null(durableCheck.AnnotationSceneUnitX); Assert.Null(durableCheck.AnnotationSceneUnitY);
    }

    [Fact]
    public async Task DragAndRemoval_PreserveImportedMetadata_AndRemovalClearsOnlyManagedState()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", InGameId = "exit-id", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6, AnnotationSceneUnitX = 10, AnnotationSceneUnitY = 11, EnableAnnotation = true };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", InGameId = "check-id", InGamePositionX = 7, InGamePositionY = 8, InGamePositionZ = 9, LocalPositionX = 10, LocalPositionY = 11, LocalPositionZ = 12, EnableAnnotation = true, AnnotationSceneUnitX = 13, AnnotationSceneUnitY = 14 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition, check); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture));
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.MoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition), 20, 21)).Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.MoveCheckAnnotationAsync(room.Id, CheckBaseline(check), 22, 23)).Status);
        await using (var moved = fixture.CreateDbContext()) { transition = await moved.RoomTransitions.SingleAsync(x => x.Id == transition.Id); check = await moved.CheckLocations.SingleAsync(x => x.Id == check.Id); }
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.RemoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition))).Status);
        Assert.Equal(V2CheckCommandStatus.Committed, (await commands.RemoveCheckAnnotationAsync(room.Id, CheckBaseline(check))).Status);
        await using var verify = fixture.CreateDbContext();
        var savedTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
        var savedCheck = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
        Assert.False(savedTransition.EnableAnnotation); Assert.Equal((20d, 21d), (savedTransition.AnnotationSceneUnitX, savedTransition.AnnotationSceneUnitY));
        Assert.Equal(("exit-id", 1d, 2d, 3d, 4d, 5d, 6d), (savedTransition.InGameId, savedTransition.InGamePositionX, savedTransition.InGamePositionY, savedTransition.InGamePositionZ, savedTransition.LocalPositionX, savedTransition.LocalPositionY, savedTransition.LocalPositionZ));
        Assert.False(savedCheck.EnableAnnotation); Assert.Equal((22d, 23d), (savedCheck.AnnotationSceneUnitX, savedCheck.AnnotationSceneUnitY));
        Assert.Equal(("check-id", 7d, 8d, 9d, 10d, 11d, 12d), (savedCheck.InGameId, savedCheck.InGamePositionX, savedCheck.InGamePositionY, savedCheck.InGamePositionZ, savedCheck.LocalPositionX, savedCheck.LocalPositionY, savedCheck.LocalPositionZ));
    }

    [Theory]
    [InlineData("transition", "disabled")]
    [InlineData("transition", "partial")]
    [InlineData("transition", "wrong-room")]
    [InlineData("check", "disabled")]
    [InlineData("check", "partial")]
    [InlineData("check", "wrong-room")]
    public async Task MoveAdmission_RejectsDisabledUnusableOrWrongRoom_BeforeMutation(string kind, string state)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", EnableAnnotation = state != "disabled", AnnotationSceneUnitX = state == "partial" ? 1 : null, AnnotationSceneUnitY = null, InGamePositionX = 2, InGamePositionY = 3 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", EnableAnnotation = state != "disabled", AnnotationSceneUnitX = state == "partial" ? 1 : null, AnnotationSceneUnitY = null, InGamePositionX = 2, InGamePositionY = 3 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, other, transition, check); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture));
        var requestedRoom = state == "wrong-room" ? other.Id : room.Id;
        if (kind == "transition")
            Assert.NotEqual(V2TransitionCommandStatus.Committed, (await commands.MoveTransitionAnnotationAsync(requestedRoom, TransitionBaseline(transition), 10, 11)).Status);
        else
            Assert.NotEqual(V2CheckCommandStatus.Committed, (await commands.MoveCheckAnnotationAsync(requestedRoom, CheckBaseline(check), 10, 11)).Status);

        await using var verify = fixture.CreateDbContext();
        var savedTransition = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
        var savedCheck = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
        Assert.Equal((transition.EnableAnnotation, transition.AnnotationSceneUnitX, transition.AnnotationSceneUnitY, transition.UpdatedUtc), (savedTransition.EnableAnnotation, savedTransition.AnnotationSceneUnitX, savedTransition.AnnotationSceneUnitY, savedTransition.UpdatedUtc));
        Assert.Equal((check.EnableAnnotation, check.AnnotationSceneUnitX, check.AnnotationSceneUnitY, check.UpdatedUtc), (savedCheck.EnableAnnotation, savedCheck.AnnotationSceneUnitX, savedCheck.AnnotationSceneUnitY, savedCheck.UpdatedUtc));
    }

    [Theory]
    [InlineData("transition", "place")]
    [InlineData("transition", "remove")]
    [InlineData("transition", "show")]
    [InlineData("transition", "move")]
    [InlineData("check", "place")]
    [InlineData("check", "remove")]
    [InlineData("check", "show")]
    [InlineData("check", "move")]
    public async Task EveryAnnotationRoute_RejectsWrongRoomBeforeMutation(string kind, string operation)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" }; var other = new Room { FriendlyName = "other", ReferenceId = "other" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 1, AnnotationSceneUnitY = 2 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 1, AnnotationSceneUnitY = 2 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, other, transition, check); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture));
        if (kind == "transition")
        {
            var result = operation switch { "place" => await commands.PlaceTransitionAnnotationAsync(other.Id, TransitionBaseline(transition), 3, 4), "remove" => await commands.RemoveTransitionAnnotationAsync(other.Id, TransitionBaseline(transition)), "show" => await commands.ShowTransitionAnnotationAsync(other.Id, TransitionBaseline(transition)), _ => await commands.MoveTransitionAnnotationAsync(other.Id, TransitionBaseline(transition), 3, 4) };
            Assert.Equal(V2TransitionCommandStatus.Missing, result.Status);
        }
        else
        {
            var result = operation switch { "place" => await commands.PlaceCheckAnnotationAsync(other.Id, CheckBaseline(check), 3, 4), "remove" => await commands.RemoveCheckAnnotationAsync(other.Id, CheckBaseline(check)), "show" => await commands.ShowCheckAnnotationAsync(other.Id, CheckBaseline(check)), _ => await commands.MoveCheckAnnotationAsync(other.Id, CheckBaseline(check), 3, 4) };
            Assert.Equal(V2CheckCommandStatus.Missing, result.Status);
        }
        await using var verify = fixture.CreateDbContext();
        Assert.Equal((true, (double?)1, (double?)2, transition.UpdatedUtc), await verify.RoomTransitions.Where(x => x.Id == transition.Id).Select(x => new ValueTuple<bool, double?, double?, DateTime>(x.EnableAnnotation, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.UpdatedUtc)).SingleAsync());
        Assert.Equal((true, (double?)1, (double?)2, check.UpdatedUtc), await verify.CheckLocations.Where(x => x.Id == check.Id).Select(x => new ValueTuple<bool, double?, double?, DateTime>(x.EnableAnnotation, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.UpdatedUtc)).SingleAsync());
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("check")]
    public async Task AnnotationMoveNoopAndOverlapConflict_PreserveTheCompleteOverridePairAndAllMetadata(string kind)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 10, AnnotationSceneUnitY = 11, InGameId = "exit-id", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 10, AnnotationSceneUnitY = 11, InGameId = "check-id", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition, check); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture));
        if (kind == "transition")
            Assert.Equal(V2TransitionCommandStatus.Unchanged, (await commands.MoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition), 10, 11)).Status);
        else
            Assert.Equal(V2CheckCommandStatus.Unchanged, (await commands.MoveCheckAnnotationAsync(room.Id, CheckBaseline(check), 10, 11)).Status);
        await using (var concurrent = fixture.CreateDbContext())
        {
            if (kind == "transition") { var row = await concurrent.RoomTransitions.SingleAsync(x => x.Id == transition.Id); row.AnnotationSceneUnitX = 12; row.UpdatedUtc = row.UpdatedUtc.AddTicks(1); }
            else { var row = await concurrent.CheckLocations.SingleAsync(x => x.Id == check.Id); row.AnnotationSceneUnitX = 12; row.UpdatedUtc = row.UpdatedUtc.AddTicks(1); }
            await concurrent.SaveChangesAsync();
        }
        if (kind == "transition")
            Assert.Equal(V2TransitionCommandStatus.Conflict, (await commands.MoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition), 20, 21)).Status);
        else
            Assert.Equal(V2CheckCommandStatus.Conflict, (await commands.MoveCheckAnnotationAsync(room.Id, CheckBaseline(check), 20, 21)).Status);
        await using var verify = fixture.CreateDbContext();
        if (kind == "transition")
        {
            var row = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
            Assert.Equal((true, (double?)12, (double?)11, "exit-id", (double?)1, (double?)2, (double?)3, (double?)4, (double?)5, (double?)6), (row.EnableAnnotation, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ));
        }
        else
        {
            var row = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
            Assert.Equal((true, (double?)12, (double?)11, "check-id", (double?)1, (double?)2, (double?)3, (double?)4, (double?)5, (double?)6), (row.EnableAnnotation, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ));
        }
    }

    [Theory]
    [InlineData("transition", "place")]
    [InlineData("transition", "move")]
    [InlineData("transition", "remove")]
    [InlineData("transition", "show")]
    [InlineData("check", "place")]
    [InlineData("check", "move")]
    [InlineData("check", "remove")]
    [InlineData("check", "show")]
    public async Task EveryAnnotationRoute_NoopOrIneligibleNoop_PreservesCompletePairsMetadataAndTimestamp(string kind, string operation)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room" };
        var enabled = operation != "remove";
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", Requirements = "r", EnableAnnotation = enabled, AnnotationSceneUnitX = 10, AnnotationSceneUnitY = 11, InGameId = "exit-id", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", EnableAnnotation = enabled, AnnotationSceneUnitX = 10, AnnotationSceneUnitY = 11, InGameId = "check-id", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition, check); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture));
        if (kind == "transition")
        {
            var result = operation switch { "place" => await commands.PlaceTransitionAnnotationAsync(room.Id, TransitionBaseline(transition), 10, 11), "move" => await commands.MoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition), 10, 11), "remove" => await commands.RemoveTransitionAnnotationAsync(room.Id, TransitionBaseline(transition)), _ => await commands.ShowTransitionAnnotationAsync(room.Id, TransitionBaseline(transition)) };
            Assert.NotEqual(V2TransitionCommandStatus.Committed, result.Status);
            await using var verify = fixture.CreateDbContext(); var row = await verify.RoomTransitions.SingleAsync(x => x.Id == transition.Id);
            Assert.Equal((transition.EnableAnnotation, transition.AnnotationSceneUnitX, transition.AnnotationSceneUnitY, transition.InGameId, transition.InGamePositionX, transition.InGamePositionY, transition.InGamePositionZ, transition.LocalPositionX, transition.LocalPositionY, transition.LocalPositionZ, transition.UpdatedUtc), (row.EnableAnnotation, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ, row.UpdatedUtc));
        }
        else
        {
            var result = operation switch { "place" => await commands.PlaceCheckAnnotationAsync(room.Id, CheckBaseline(check), 10, 11), "move" => await commands.MoveCheckAnnotationAsync(room.Id, CheckBaseline(check), 10, 11), "remove" => await commands.RemoveCheckAnnotationAsync(room.Id, CheckBaseline(check)), _ => await commands.ShowCheckAnnotationAsync(room.Id, CheckBaseline(check)) };
            Assert.NotEqual(V2CheckCommandStatus.Committed, result.Status);
            await using var verify = fixture.CreateDbContext(); var row = await verify.CheckLocations.SingleAsync(x => x.Id == check.Id);
            Assert.Equal((check.EnableAnnotation, check.AnnotationSceneUnitX, check.AnnotationSceneUnitY, check.InGameId, check.InGamePositionX, check.InGamePositionY, check.InGamePositionZ, check.LocalPositionX, check.LocalPositionY, check.LocalPositionZ, check.UpdatedUtc), (row.EnableAnnotation, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY, row.InGameId, row.InGamePositionX, row.InGamePositionY, row.InGamePositionZ, row.LocalPositionX, row.LocalPositionY, row.LocalPositionZ, row.UpdatedUtc));
        }
    }

    private static TransitionDurableBaseline TransitionBaseline(RoomTransition x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified, x.EnableAnnotation);
    private static CheckMetadataDurableBaseline CheckBaseline(CheckLocation x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.IsIncludedInApworld, x.EnableAnnotation, x.IsTodo, x.IsVerified, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
}
