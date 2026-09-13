using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite coverage for the complete typed transition adapter.</summary>
public sealed class RoomEditorV2TransitionCommandTests
{
    [Fact]
    public async Task SaveCreateLifecycleAndDelete_UseCompleteTypedSnapshots()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var row = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "old", Requirements = "old", Notes = "old", SortOrder = 0 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, row); await db.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var baseline = Baseline(row);
        var draft = Draft("b", "new", "game", 1, 2, 3, 4, 5, 6, 7, 8, "source", "dest", "exit", "req", "note", true, true);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.SaveTransitionAsync(room.Id, baseline, draft)).Status);
        await using (var db = fixture.CreateDbContext()) { var saved = await db.RoomTransitions.SingleAsync(x => x.Id == row.Id); Assert.Equal(("b", "new", "game", 1d, 2d, 3d, 4d, 5d, 6d, 7d, 8d, "source", "dest", "exit", "req", "note", true, true), (saved.Alias, saved.FriendlyName, saved.InGameId, saved.InGamePositionX, saved.InGamePositionY, saved.InGamePositionZ, saved.LocalPositionX, saved.LocalPositionY, saved.LocalPositionZ, saved.AnnotationSceneUnitX, saved.AnnotationSceneUnitY, saved.SourceSubroomReferenceText, saved.DestinationRoomReferenceText, saved.DestinationTransitionAliasText, saved.Requirements, saved.Notes, saved.IsTodo, saved.IsVerified)); baseline = Baseline(saved); }
        Assert.Equal(V2TransitionCommandStatus.Unchanged, (await commands.SaveTransitionAsync(room.Id, baseline, draft)).Status);
        var created = await commands.CreateTransitionAsync(room.Id, Draft("c", "created", "meta", 9, 9, 9, 9, 9, 9, 9, 9, null, null, null, "r", "n", false, null));
        Assert.Equal(V2TransitionCommandStatus.Committed, created.Status);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.SetTransitionArchiveAsync(room.Id, row.Id, true)).Status);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ReorderTransitionAsync(room.Id, row.Id, 0)).Status);
        Assert.Equal(V2TransitionCommandStatus.ExpectedFailure, (await commands.DeleteTransitionAsync(room.Id, created.CreatedEntityId!.Value)).Status);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.DeleteTransitionAsync(room.Id, row.Id)).Status);
        await using var verify = fixture.CreateDbContext(); Assert.Null(await verify.RoomTransitions.SingleOrDefaultAsync(x => x.Id == row.Id));
    }

    [Fact]
    public async Task MetadataSave_PersistsNullableCoordinatesMergesDisjointAndRejectsOverlapOrMissing()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var row = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "authored", Requirements = "requirements", Notes = "notes", SortOrder = 0, ResolvedDestinationRoomId = room.Id };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(room, row); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var baseline = Baseline(row);
        var metadata = new TransitionInGameMetadataDraft("game", 1, null, 3, null, 5, null, 7, null);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.SaveTransitionMetadataAsync(room.Id, baseline, metadata)).Status);
        await using (var verify = fixture.CreateDbContext())
        {
            var saved = await verify.RoomTransitions.SingleAsync(x => x.Id == row.Id);
            Assert.Equal(("game", 1d, null, 3d, null, 5d, null, 7d, null), (saved.InGameId, saved.InGamePositionX, saved.InGamePositionY, saved.InGamePositionZ, saved.LocalPositionX, saved.LocalPositionY, saved.LocalPositionZ, saved.AnnotationSceneUnitX, saved.AnnotationSceneUnitY));
            Assert.Equal(("a", "authored", "requirements", "notes", room.Id), (saved.Alias, saved.FriendlyName, saved.Requirements, saved.Notes, saved.ResolvedDestinationRoomId));
            baseline = Baseline(saved);
        }
        Assert.Equal(V2TransitionCommandStatus.Unchanged, (await commands.SaveTransitionMetadataAsync(room.Id, baseline, metadata)).Status);
        await using (var external = fixture.CreateDbContext()) { var saved = await external.RoomTransitions.SingleAsync(x => x.Id == row.Id); saved.Notes = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.SaveTransitionMetadataAsync(room.Id, baseline, metadata with { AnnotationSceneUnitY = 9 })).Status);
        var stale = await ReadAsync(fixture, row.Id);
        await using (var external = fixture.CreateDbContext()) { var saved = await external.RoomTransitions.SingleAsync(x => x.Id == row.Id); saved.InGameId = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Conflict, (await commands.SaveTransitionMetadataAsync(room.Id, Baseline(stale), metadata with { InGameId = "local" })).Status);
        await using (var remove = fixture.CreateDbContext()) { remove.Remove(await remove.RoomTransitions.SingleAsync(x => x.Id == row.Id)); await remove.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Missing, (await commands.SaveTransitionMetadataAsync(room.Id, Baseline(stale), metadata)).Status);
    }

    [Fact]
    public async Task MetadataSave_NoOpPreservesTimestamp_ClearsPopulatedValues_AndUsesOneAtomicSqliteUpdate()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SortOrder = 0 };
        var row = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "metadata", Requirements = "r", SortOrder = 0,
            InGameId = "game", InGamePositionX = 1, InGamePositionY = 2, InGamePositionZ = 3, LocalPositionX = 4, LocalPositionY = 5, LocalPositionZ = 6, AnnotationSceneUnitX = 7, AnnotationSceneUnitY = 8 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(room, row); await seed.SaveChangesAsync(); }
        var trace = new TransitionWriteTrace(); var factory = new TracingFactory(fixture.DatabasePath, trace);
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(factory), factory);
        var baseline = Baseline(row);
        var clear = new TransitionInGameMetadataDraft(null, null, null, null, null, null, null, null, null);

        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.SaveTransitionMetadataAsync(room.Id, baseline, clear)).Status);
        await using (var verify = fixture.CreateDbContext())
        {
            var saved = await verify.RoomTransitions.SingleAsync(x => x.Id == row.Id);
            Assert.Equal((string?)null, saved.InGameId);
            Assert.Equal((double?)null, saved.InGamePositionX); Assert.Equal((double?)null, saved.InGamePositionY); Assert.Equal((double?)null, saved.InGamePositionZ);
            Assert.Equal((double?)null, saved.LocalPositionX); Assert.Equal((double?)null, saved.LocalPositionY); Assert.Equal((double?)null, saved.LocalPositionZ);
            Assert.Equal((double?)null, saved.AnnotationSceneUnitX); Assert.Equal((double?)null, saved.AnnotationSceneUnitY);
            baseline = Baseline(saved);
        }
        Assert.Single(trace.Commands, x => x.Contains("UPDATE \"RoomTransitions\"", StringComparison.Ordinal));

        var timestamp = baseline.UpdatedUtc;
        trace.Commands.Clear();
        Assert.Equal(V2TransitionCommandStatus.Unchanged, (await commands.SaveTransitionMetadataAsync(room.Id, baseline, clear)).Status);
        await using var noOp = fixture.CreateDbContext();
        Assert.Equal(timestamp, (await noOp.RoomTransitions.SingleAsync(x => x.Id == row.Id)).UpdatedUtc);
        Assert.DoesNotContain(trace.Commands, x => x.Contains("UPDATE \"RoomTransitions\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InverseProposal_UsesCurrentDraftAndAtomicallyFillsOnlyBlankTargetFields()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", Requirements = "r", SortOrder = 0 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", DestinationRoomReferenceText = "preserve", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, source, target); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var draft = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "note", true, null);
        var prepared = await commands.PrepareInverseAsync(sourceRoom.Id, Baseline(source), draft);
        Assert.Equal(V2TransitionCommandStatus.Proposal, prepared.Status);
        Assert.False(prepared.Proposal!.Target.FillDestinationRoomReferenceText); Assert.True(prepared.Proposal.Target.FillDestinationTransitionAliasText);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ApplyInverseAsync(sourceRoom.Id, prepared.Proposal, true)).Status);
        await using var verify = fixture.CreateDbContext();
        var savedSource = await verify.RoomTransitions.SingleAsync(x => x.Id == source.Id);
        var savedTarget = await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id);
        Assert.Equal(("target", "in", "note", true), (savedSource.DestinationRoomReferenceText, savedSource.DestinationTransitionAliasText, savedSource.Notes, savedSource.IsTodo));
        Assert.Equal(("preserve", "out"), (savedTarget.DestinationRoomReferenceText, savedTarget.DestinationTransitionAliasText));
    }

    [Fact]
    public async Task InverseDoNotUpdateRevertAndStaleTargetConflict_PreserveAtomicAuthoredText()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", Requirements = "r", SortOrder = 0 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, source, target); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var draft = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "changed", false, null);
        var proposal = (await commands.PrepareInverseAsync(sourceRoom.Id, Baseline(source), draft)).Proposal!;
        var reverted = commands.RevertInverse(proposal);
        Assert.Equal(V2TransitionCommandStatus.Unchanged, reverted.Status);
        Assert.Equal(new TransitionDraft(draft.ClientDraftId, source.Alias, source.FriendlyName, source.InGameId, source.InGamePositionX, source.InGamePositionY, source.InGamePositionZ, source.LocalPositionX, source.LocalPositionY, source.LocalPositionZ, source.AnnotationSceneUnitX, source.AnnotationSceneUnitY, source.SourceSubroomReferenceText, source.DestinationRoomReferenceText, source.DestinationTransitionAliasText, source.Requirements, source.Notes, source.IsTodo, source.IsVerified), reverted.RetainedDraft);
        await using (var noWrite = fixture.CreateDbContext()) { var unchanged = await noWrite.RoomTransitions.SingleAsync(x => x.Id == source.Id); Assert.Equal((source.UpdatedUtc, source.Notes, source.DestinationRoomReferenceText, source.DestinationTransitionAliasText), (unchanged.UpdatedUtc, unchanged.Notes, unchanged.DestinationRoomReferenceText, unchanged.DestinationTransitionAliasText)); }
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ApplyInverseAsync(sourceRoom.Id, proposal, false)).Status);
        await using (var afterDecline = fixture.CreateDbContext()) { Assert.Equal("changed", (await afterDecline.RoomTransitions.SingleAsync(x => x.Id == source.Id)).Notes); Assert.Null((await afterDecline.RoomTransitions.SingleAsync(x => x.Id == target.Id)).DestinationRoomReferenceText); }

        var current = await ReadAsync(fixture, source.Id); var retry = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, " target ", "in", "r", "retry", false, null);
        var staleProposal = (await commands.PrepareInverseAsync(sourceRoom.Id, Baseline(current), retry)).Proposal!;
        await using (var external = fixture.CreateDbContext()) { var changed = await external.RoomTransitions.SingleAsync(x => x.Id == target.Id); changed.Notes = "external"; await external.SaveChangesAsync(); }
        var conflict = await commands.ApplyInverseAsync(sourceRoom.Id, staleProposal, true);
        Assert.Equal(V2TransitionCommandStatus.Conflict, conflict.Status);
        await using var verify = fixture.CreateDbContext(); Assert.Equal("changed", (await verify.RoomTransitions.SingleAsync(x => x.Id == source.Id)).Notes); Assert.Null((await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).DestinationRoomReferenceText);
    }

    [Fact]
    public async Task QualifyingCreateInverseProposal_IsNoWriteAndItsThreeOutcomesPreserveCompleteSnapshots()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        var declineTarget = new RoomTransition { RoomId = targetRoom.Id, Alias = "in2", FriendlyName = "In two", Requirements = "r", SortOrder = 1 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, target, declineTarget); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var draft = Draft("out", "Out", "game", 1, 2, 3, 4, 5, 6, 7, 8, null, "target", "in", "requirements", "notes", true, false);

        var prepared = await commands.CreateTransitionAsync(sourceRoom.Id, draft);
        Assert.Equal(V2TransitionCommandStatus.Proposal, prepared.Status);
        Assert.DoesNotContain(await ReadAllAsync(fixture), x => x.RoomId == sourceRoom.Id);
        Assert.True(prepared.CreateProposal!.Target.FillDestinationRoomReferenceText);
        Assert.True(prepared.CreateProposal.Target.FillDestinationTransitionAliasText);
        var reverted = commands.RevertCreateInverse(prepared.CreateProposal);
        Assert.Equal(V2TransitionCommandStatus.Unchanged, reverted.Status);
        Assert.Equal(draft, reverted.RetainedDraft);
        Assert.DoesNotContain(await ReadAllAsync(fixture), x => x.RoomId == sourceRoom.Id);
        await using (var noWrite = fixture.CreateDbContext()) { var unchangedTarget = await noWrite.RoomTransitions.SingleAsync(x => x.Id == target.Id); Assert.Equal((target.UpdatedUtc, null, null), (unchangedTarget.UpdatedUtc, unchangedTarget.DestinationRoomReferenceText, unchangedTarget.DestinationTransitionAliasText)); }

        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ApplyCreateInverseAsync(sourceRoom.Id, prepared.CreateProposal, true)).Status);
        await using (var verify = fixture.CreateDbContext())
        {
            var source = await verify.RoomTransitions.SingleAsync(x => x.RoomId == sourceRoom.Id);
            var savedTarget = await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            Assert.Equal((draft.Alias, draft.FriendlyName, draft.InGameId, draft.InGamePositionX, draft.InGamePositionY, draft.InGamePositionZ, draft.LocalPositionX, draft.LocalPositionY, draft.LocalPositionZ, draft.AnnotationSceneUnitX, draft.AnnotationSceneUnitY, draft.SourceSubroomReferenceText, draft.DestinationRoomReferenceText, draft.DestinationTransitionAliasText, draft.Requirements, draft.Notes, draft.IsTodo, draft.IsVerified), (source.Alias, source.FriendlyName, source.InGameId, source.InGamePositionX, source.InGamePositionY, source.InGamePositionZ, source.LocalPositionX, source.LocalPositionY, source.LocalPositionZ, source.AnnotationSceneUnitX, source.AnnotationSceneUnitY, source.SourceSubroomReferenceText, source.DestinationRoomReferenceText, source.DestinationTransitionAliasText, source.Requirements, source.Notes, source.IsTodo, source.IsVerified));
            Assert.Equal(("source", "out", sourceRoom.Id, source.Id), (savedTarget.DestinationRoomReferenceText, savedTarget.DestinationTransitionAliasText, savedTarget.ResolvedDestinationRoomId, savedTarget.ResolvedDestinationTransitionId));
            Assert.Equal((targetRoom.Id, target.Id), (source.ResolvedDestinationRoomId, source.ResolvedDestinationTransitionId));
        }

        var decline = Draft("other", "Other", null, null, null, null, null, null, null, null, null, null, "target", "in2", "r", "n", false, null);
        var declineProposal = (await commands.CreateTransitionAsync(sourceRoom.Id, decline)).CreateProposal!;
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ApplyCreateInverseAsync(sourceRoom.Id, declineProposal, false)).Status);
        await using var afterDecline = fixture.CreateDbContext();
        Assert.Null((await afterDecline.RoomTransitions.SingleAsync(x => x.Id == declineTarget.Id)).DestinationRoomReferenceText);
        Assert.Equal(2, await afterDecline.RoomTransitions.CountAsync(x => x.RoomId == sourceRoom.Id));
    }

    [Fact]
    public async Task CreateInverse_StaleTargetConflictsWithoutCreatingSourceOrOverwritingTarget()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, target); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var draft = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "n", false, null);
        var proposal = (await commands.CreateTransitionAsync(sourceRoom.Id, draft)).CreateProposal!;

        await using (var external = fixture.CreateDbContext())
        {
            var changed = await external.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            changed.DestinationRoomReferenceText = "external";
            await external.SaveChangesAsync();
        }

        var result = await commands.ApplyCreateInverseAsync(sourceRoom.Id, proposal, true);

        Assert.Equal(V2TransitionCommandStatus.Conflict, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.RoomTransitions.CountAsync(x => x.RoomId == sourceRoom.Id));
        var unchangedTarget = await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id);
        Assert.Equal(("external", (string?)null), (unchangedTarget.DestinationRoomReferenceText, unchangedTarget.DestinationTransitionAliasText));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateInverse_ArchivedOrDeletedTargetConflictsWithoutCreatingSourceOrMutatingTarget(bool permanentlyDeleteTarget)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, target); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var draft = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "n", false, null);
        var proposal = (await commands.CreateTransitionAsync(sourceRoom.Id, draft)).CreateProposal!;

        await using (var external = fixture.CreateDbContext())
        {
            var staleTarget = await external.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            staleTarget.IsArchived = true;
            staleTarget.ArchivedUtc = DateTime.UtcNow;
            if (permanentlyDeleteTarget) external.Remove(staleTarget);
            await external.SaveChangesAsync();
        }

        var result = await commands.ApplyCreateInverseAsync(sourceRoom.Id, proposal, true);

        Assert.Equal(V2TransitionCommandStatus.Conflict, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.RoomTransitions.CountAsync(x => x.RoomId == sourceRoom.Id));
        if (permanentlyDeleteTarget)
            Assert.Null(await verify.RoomTransitions.SingleOrDefaultAsync(x => x.Id == target.Id));
        else
        {
            var archivedTarget = await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            Assert.True(archivedTarget.IsArchived);
            Assert.Equal((string?)null, archivedTarget.DestinationRoomReferenceText);
            Assert.Equal((string?)null, archivedTarget.DestinationTransitionAliasText);
        }
    }

    [Fact]
    public async Task CreateInverse_IsIneligibleForArchivedOrAmbiguousSourceOrDestinationAndDeleteCleansResolverIds()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, target, new Room { FriendlyName = "duplicate", ReferenceId = "source", SortOrder = 2 }); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var draft = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "n", false, null);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.CreateTransitionAsync(sourceRoom.Id, draft)).Status);
        await using (var archive = fixture.CreateDbContext()) { var room = await archive.Rooms.SingleAsync(x => x.Id == targetRoom.Id); room.IsArchived = true; room.ArchivedUtc = DateTime.UtcNow; await archive.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.CreateTransitionAsync(sourceRoom.Id, Draft("two", "Two", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "n", false, null))).Status);

        await using (var lifecycle = fixture.CreateDbContext())
        {
            var inbound = await lifecycle.RoomTransitions.FirstAsync(x => x.RoomId == sourceRoom.Id);
            inbound.ResolvedDestinationTransitionId = target.Id;
            var archivedTarget = await lifecycle.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            archivedTarget.IsArchived = true; archivedTarget.ArchivedUtc = DateTime.UtcNow;
            await lifecycle.SaveChangesAsync();
        }
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.DeleteTransitionAsync(targetRoom.Id, target.Id)).Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Null((await verify.RoomTransitions.FirstAsync(x => x.RoomId == sourceRoom.Id)).ResolvedDestinationTransitionId);
    }

    [Fact]
    public async Task InversePreparation_RejectsAmbiguousDraftDestinationWithoutCatalogueEntityMaterialization()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var one = new Room { FriendlyName = "One", ReferenceId = "target", SortOrder = 1 };
        var two = new Room { FriendlyName = "Two", ReferenceId = "target", SortOrder = 2 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, one, two, source); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var result = await commands.PrepareInverseAsync(sourceRoom.Id, Baseline(source), Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "changed", false, null));
        Assert.Equal(V2TransitionCommandStatus.Committed, result.Status);
    }

    [Fact]
    public async Task InverseEdit_SourceOverlapConflictsWithoutWriteAndDisjointSourceChangeMerges()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", Requirements = "r", SortOrder = 0 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, source, target); await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var overlap = (await commands.PrepareInverseAsync(sourceRoom.Id, Baseline(source), Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "local", false, null))).Proposal!;
        await using (var external = fixture.CreateDbContext()) { var row = await external.RoomTransitions.SingleAsync(x => x.Id == source.Id); row.Notes = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Conflict, (await commands.ApplyInverseAsync(sourceRoom.Id, overlap, true)).Status);
        await using (var check = fixture.CreateDbContext()) { Assert.Equal("external", (await check.RoomTransitions.SingleAsync(x => x.Id == source.Id)).Notes); Assert.Null((await check.RoomTransitions.SingleAsync(x => x.Id == target.Id)).DestinationRoomReferenceText); }

        var durable = await ReadAsync(fixture, source.Id);
        var merge = (await commands.PrepareInverseAsync(sourceRoom.Id, Baseline(durable), Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "merged", false, null))).Proposal!;
        await using (var external = fixture.CreateDbContext()) { var row = await external.RoomTransitions.SingleAsync(x => x.Id == source.Id); row.FriendlyName = "external name"; await external.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ApplyInverseAsync(sourceRoom.Id, merge, false)).Status);
        await using var merged = fixture.CreateDbContext(); var saved = await merged.RoomTransitions.SingleAsync(x => x.Id == source.Id); Assert.Equal(("external name", "merged"), (saved.FriendlyName, saved.Notes));
    }

    [Fact]
    public async Task ExplicitInverseStatusPreparation_IsNoWrite_AndApplyIsTargetOnlyWithConcurrentAndInapplicableProtection()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "in", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = targetRoom.Id };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = sourceRoom.Id };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, source, target); await seed.SaveChangesAsync(); source.ResolvedDestinationTransitionId = target.Id; target.ResolvedDestinationTransitionId = source.Id; await seed.SaveChangesAsync(); }
        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var dirty = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "dirty", false, null);
        var beforeSource = (await ReadAsync(fixture, source.Id)).UpdatedUtc; var beforeTarget = (await ReadAsync(fixture, target.Id)).UpdatedUtc;
        var prepared = await commands.PrepareInverseStatusActionAsync(sourceRoom.Id, Baseline(source), dirty);
        Assert.Equal(V2TransitionCommandStatus.Proposal, prepared.Status);
        await using (var noWrite = fixture.CreateDbContext()) { var persistedSource = await noWrite.RoomTransitions.SingleAsync(x => x.Id == source.Id); var persistedTarget = await noWrite.RoomTransitions.SingleAsync(x => x.Id == target.Id); Assert.Equal(("r", (string?)null, beforeSource, beforeTarget), (persistedSource.Requirements, persistedTarget.DestinationRoomReferenceText, persistedSource.UpdatedUtc, persistedTarget.UpdatedUtc)); }
        Assert.Equal(V2TransitionCommandStatus.Unchanged, (await commands.ApplyInverseAsync(sourceRoom.Id, prepared.Proposal!, false)).Status);
        Assert.Equal(dirty, commands.RevertInverse(prepared.Proposal!).RetainedDraft);
        Assert.Equal(V2TransitionCommandStatus.Committed, (await commands.ApplyInverseAsync(sourceRoom.Id, prepared.Proposal!, true)).Status);
        await using (var applied = fixture.CreateDbContext())
        {
            var savedSource = await applied.RoomTransitions.SingleAsync(x => x.Id == source.Id); var savedTarget = await applied.RoomTransitions.SingleAsync(x => x.Id == target.Id);
            Assert.Equal("r", savedSource.Requirements); Assert.Equal(("source", "out", sourceRoom.Id, source.Id), (savedTarget.DestinationRoomReferenceText, savedTarget.DestinationTransitionAliasText, savedTarget.ResolvedDestinationRoomId, savedTarget.ResolvedDestinationTransitionId));
        }
        var freshSource = await ReadAsync(fixture, source.Id);
        Assert.Equal(V2TransitionCommandStatus.ExpectedFailure, (await commands.PrepareInverseStatusActionAsync(sourceRoom.Id, Baseline(freshSource), dirty)).Status);

        await using (var reset = fixture.CreateDbContext()) { var row = await reset.RoomTransitions.SingleAsync(x => x.Id == target.Id); row.DestinationRoomReferenceText = null; row.DestinationTransitionAliasText = null; await reset.SaveChangesAsync(); }
        var concurrent = (await commands.PrepareInverseStatusActionAsync(sourceRoom.Id, Baseline(await ReadAsync(fixture, source.Id)), dirty)).Proposal!;
        await using (var external = fixture.CreateDbContext()) { var row = await external.RoomTransitions.SingleAsync(x => x.Id == target.Id); row.DestinationRoomReferenceText = "external"; await external.SaveChangesAsync(); }
        Assert.Equal(V2TransitionCommandStatus.Conflict, (await commands.ApplyInverseAsync(sourceRoom.Id, concurrent, true)).Status);
        await using var verify = fixture.CreateDbContext(); Assert.Equal(("r", "external", (string?)null), ((await verify.RoomTransitions.SingleAsync(x => x.Id == source.Id)).Requirements, (await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).DestinationRoomReferenceText, (await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id)).DestinationTransitionAliasText));
    }

    [Fact]
    public async Task ExplicitInverseStatusApply_SourceChangedAfterPreparation_AbortsWithoutTargetWrite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "in", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = targetRoom.Id };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = sourceRoom.Id };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.AddRange(sourceRoom, targetRoom, source, target); await seed.SaveChangesAsync();
            source.ResolvedDestinationTransitionId = target.Id; target.ResolvedDestinationTransitionId = source.Id; await seed.SaveChangesAsync();
        }

        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var dirty = Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "dirty", false, null);
        var proposal = (await commands.PrepareInverseStatusActionAsync(sourceRoom.Id, Baseline(await ReadAsync(fixture, source.Id)), dirty)).Proposal!;
        await using (var external = fixture.CreateDbContext())
        {
            var changedSource = await external.RoomTransitions.SingleAsync(x => x.Id == source.Id);
            changedSource.Alias = "changed";
            await external.SaveChangesAsync();
        }

        Assert.Equal(V2TransitionCommandStatus.Conflict, (await commands.ApplyInverseAsync(sourceRoom.Id, proposal, true)).Status);
        await using var verify = fixture.CreateDbContext();
        var persistedSource = await verify.RoomTransitions.SingleAsync(x => x.Id == source.Id);
        var persistedTarget = await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id);
        Assert.Equal("changed", persistedSource.Alias);
        Assert.Null(persistedTarget.DestinationRoomReferenceText);
        Assert.Null(persistedTarget.DestinationTransitionAliasText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInverseStatusPreparation_RejectsSourceReferenceOrAliasAmbiguityWithoutWrite(bool aliasAmbiguity)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Out", DestinationRoomReferenceText = "target", DestinationTransitionAliasText = "in", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = targetRoom.Id };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0, ResolvedDestinationRoomId = sourceRoom.Id };
        await using (var seed = fixture.CreateDbContext())
        {
            seed.AddRange(sourceRoom, targetRoom, source, target); await seed.SaveChangesAsync();
            source.ResolvedDestinationTransitionId = target.Id; target.ResolvedDestinationTransitionId = source.Id; await seed.SaveChangesAsync();
        }

        var commands = new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture);
        var sourceUpdatedUtc = (await ReadAsync(fixture, source.Id)).UpdatedUtc;
        await using (var external = fixture.CreateDbContext())
        {
            if (aliasAmbiguity)
                external.RoomTransitions.Add(new RoomTransition { RoomId = sourceRoom.Id, Alias = "out", FriendlyName = "Duplicate", Requirements = "r", SortOrder = 1 });
            else
                external.Rooms.Add(new Room { FriendlyName = "Duplicate", ReferenceId = "source", SortOrder = 2 });
            await external.SaveChangesAsync();
        }

        var result = await commands.PrepareInverseStatusActionAsync(sourceRoom.Id, Baseline(await ReadAsync(fixture, source.Id)),
            Draft("out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "dirty", false, null));

        Assert.Equal(V2TransitionCommandStatus.ExpectedFailure, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(sourceUpdatedUtc, (await verify.RoomTransitions.SingleAsync(x => x.Id == source.Id)).UpdatedUtc);
        var persistedTarget = await verify.RoomTransitions.SingleAsync(x => x.Id == target.Id);
        Assert.Null(persistedTarget.DestinationRoomReferenceText);
        Assert.Null(persistedTarget.DestinationTransitionAliasText);
    }

    private static async Task<RoomTransition> ReadAsync(MigratedSqliteFixture fixture, Guid id) { await using var db = fixture.CreateDbContext(); return await db.RoomTransitions.AsNoTracking().SingleAsync(x => x.Id == id); }
    private static async Task<List<RoomTransition>> ReadAllAsync(MigratedSqliteFixture fixture) { await using var db = fixture.CreateDbContext(); return await db.RoomTransitions.AsNoTracking().ToListAsync(); }

    private static TransitionDurableBaseline Baseline(RoomTransition x) => new(x.Id,x.UpdatedUtc,x.SortOrder,x.IsArchived,x.Alias,x.FriendlyName,x.InGameId,x.InGamePositionX,x.InGamePositionY,x.InGamePositionZ,x.LocalPositionX,x.LocalPositionY,x.LocalPositionZ,x.AnnotationSceneUnitX,x.AnnotationSceneUnitY,x.SourceSubroomReferenceText,x.DestinationRoomReferenceText,x.DestinationTransitionAliasText,x.Requirements,x.Notes,x.IsTodo,x.IsVerified);
    private static TransitionDraft Draft(string alias,string name,string? game,double? ix,double? iy,double? iz,double? lx,double? ly,double? lz,double? ax,double? ay,string? source,string? room,string? destination,string req,string notes,bool todo,bool? verified) => new(Guid.NewGuid(),alias,name,game,ix,iy,iz,lx,ly,lz,ax,ay,source,room,destination,req,notes,todo,verified);
    private sealed class TracingFactory(string path, TransitionWriteTrace trace) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").AddInterceptors(trace).Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class TransitionWriteTrace : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
