using System.Reflection;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Migration-current SQLite page-route evidence for archived child delete confirmation.</summary>
public sealed class RoomEditorV2ArchivedChildDeletePageRouteTests
{
    [Theory]
    [InlineData(V2DeleteConfirmationKind.Subroom)]
    [InlineData(V2DeleteConfirmationKind.Check)]
    public async Task ArchivedChildDelete_CancelWritesNothing_ConfirmDeletesWithOneSceneAndNoMap(V2DeleteConfirmationKind kind)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var (room, entityId) = await SeedAsync(fixture, kind);
        var loader = new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture));
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true);
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture)));
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.FindAll("button").Single(x => x.TextContent.Trim() == "show archived room contents").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(kind == V2DeleteConfirmationKind.Subroom
            ? "table[data-v2-subroom-table='archived']"
            : "table[data-v2-check-table='archived']")));
        await InvokeChildAction(page, kind, "open-delete");
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        await InvokeChildAction(page, kind, "cancel-delete");
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        await using (var unchanged = fixture.CreateDbContext())
            Assert.True(kind == V2DeleteConfirmationKind.Subroom
                ? await unchanged.Subrooms.AnyAsync(x => x.Id == entityId)
                : await unchanged.CheckLocations.AnyAsync(x => x.Id == entityId));

        var before = Trace(page).SceneLoaderInvocations;
        await InvokeChildAction(page, kind, "open-delete");
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));

        await using (var deleted = fixture.CreateDbContext())
            Assert.False(kind == V2DeleteConfirmationKind.Subroom
                ? await deleted.Subrooms.AnyAsync(x => x.Id == entityId)
                : await deleted.CheckLocations.AnyAsync(x => x.Id == entityId));
        var trace = Trace(page);
        Assert.Equal(before + 1, trace.SceneLoaderInvocations);
        Assert.Equal(0, trace.MapLoaderInvocations);
        Assert.Contains($"{(kind == V2DeleteConfirmationKind.Subroom ? "subroom" : "check")}-permanent-delete:complete-room-refresh", trace.Events);
    }

    /// <summary>
    /// Mirrors the browser owner contract: navigation/blur/control callbacks take
    /// a client-row identity, while a lifecycle child action takes the persisted
    /// row GUID.  Do not accidentally feed an editor field id to either boundary.
    /// </summary>
    private static async Task InvokeChildAction(IRenderedComponent<RoomEditorV2Page> page, V2DeleteConfirmationKind kind, string action)
    {
        var (rowAttribute, clientAttribute) = kind == V2DeleteConfirmationKind.Subroom
            ? ("data-v2-subroom-row", "data-v2-subroom-client-row")
            : ("data-v2-check-row", "data-v2-check-client-row");
        var row = page.FindAll($"tr[{rowAttribute}]").Single(x => Guid.TryParse(x.GetAttribute(rowAttribute), out _));
        var entityId = row.GetAttribute(rowAttribute)!;
        // Read the rendered client identity as well: this is deliberately not
        // passed to ChildAction, which requires the persisted entity identity.
        Assert.True(Guid.TryParse(row.QuerySelector($"[{clientAttribute}]")!.GetAttribute(clientAttribute), out _));
        if (kind == V2DeleteConfirmationKind.Subroom)
            await page.InvokeAsync(() => page.FindComponent<SubroomTablePresentation>().Instance.ChildActionSubroomAsync(action, entityId));
        else
            await page.InvokeAsync(() => page.FindComponent<CheckTablePresentation>().Instance.ChildActionCheckAsync(action, entityId));
    }

    private static V2RoomOperationTrace Trace(IRenderedComponent<RoomEditorV2Page> page) =>
        ((RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!).OperationTrace;

    private static async Task<(Room Room, Guid EntityId)> SeedAsync(MigratedSqliteFixture fixture, V2DeleteConfirmationKind kind)
    {
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        var subroom = new Subroom { RoomId = room.Id, FriendlyName = "Archived subroom", ReferenceId = "sub", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "Archived check", Requirements = "r", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        await using var db = fixture.CreateDbContext();
        db.AddRange(room, subroom, check); await db.SaveChangesAsync();
        return (room, kind == V2DeleteConfirmationKind.Subroom ? subroom.Id : check.Id);
    }
}
