using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>Rendered migration-current SQLite proof for the active check metadata dialog.</summary>
public sealed class RoomEditorV2CheckMetadataPageTests
{
    [Fact]
    public async Task ActiveCheckMetadataModal_RendersNineFieldsAndHeldApplyBlocksThenRefreshes()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, check) = await Seed(fixture);
        var production = new RoomEditorV2CommandService(new LogicCatalogService(fixture)); var commands = new HeldCommands(production);
        using var context = Context(fixture, commands, out var loader); var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id)); var initiator = $"v2-check-metadata-{check.Id}";
        page.Find($"#{initiator}").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog")));
        var dialog = page.Find("#v2-page-modal-dialog");
        foreach (var label in new[] { "game ID", "in-game X", "in-game Y", "in-game Z", "local X", "local Y", "local Z", "annotation scene X", "annotation scene Y" }) Assert.Equal("off", dialog.QuerySelector($"[aria-label='{label}']")!.GetAttribute("autocomplete"));
        Assert.Contains("danger", dialog.QuerySelector("[aria-label='game ID']")!.ParentElement!.ClassName);
        Set(dialog, "game ID", "held-check"); dialog.QuerySelectorAll("button").Single(x => x.TextContent!.Trim() == "Apply").Click(); await commands.Entered.Task;
        page.WaitForAssertion(() => { Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage")); Assert.All(page.Find("#v2-page-modal-dialog").QuerySelectorAll("input,button"), x => Assert.True(x.HasAttribute("disabled"))); });
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" }); page.Find(".v2-page-modal-backdrop").Click(); Assert.Single(page.FindAll("#v2-page-modal-dialog"));
        commands.Release.TrySetResult(); page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Equal(2, loader.Loads); Assert.Contains("refresh #2", page.Markup); });
        await using var verify = fixture.CreateDbContext(); Assert.Equal("held-check", (await verify.CheckLocations.SingleAsync(x => x.Id == check.Id)).InGameId);
        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == initiator));
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("backdrop")]
    public async Task CheckMetadataCancelRoutesWriteNothing(string route)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var (room, check) = await Seed(fixture); using var context = Context(fixture, new RoomEditorV2CommandService(new LogicCatalogService(fixture)), out _); var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.Find($"#v2-check-metadata-{check.Id}").Click(); page.WaitForAssertion(() => Assert.NotNull(page.Find("#v2-page-modal-dialog"))); Set(page.Find("#v2-page-modal-dialog"), "game ID", "discard");
        if(route=="cancel")page.FindAll("#v2-page-modal-dialog button").Single(x=>x.TextContent!.Trim()=="Cancel").Click(); else if(route=="escape")page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs{Key="Escape"}); else page.Find(".v2-page-modal-backdrop").Click();
        await using var verify=fixture.CreateDbContext(); page.WaitForAssertion(()=>Assert.Empty(page.FindAll("#v2-page-modal-dialog"))); Assert.Equal("duplicate",(await verify.CheckLocations.SingleAsync(x=>x.Id==check.Id)).InGameId);
    }
    [Theory]
    [InlineData(false, "v2-check-metadata-")]
    [InlineData(true, "room-friendly-name")]
    public async Task CheckMetadataConflictAndMissingRefreshThenUseSettledCorrectionFocus(bool missing,string expectedPrefix)
    {
        await using var fixture=await MigratedSqliteFixture.CreateAsync();var(room,check)=await Seed(fixture);using var context=Context(fixture,new RoomEditorV2CommandService(new LogicCatalogService(fixture)),out var loader);var page=context.RenderComponent<RoomEditorV2Page>(p=>p.Add(x=>x.RoomId,room.Id));
        page.Find($"#v2-check-metadata-{check.Id}").Click();page.WaitForAssertion(()=>Assert.NotNull(page.Find("#v2-page-modal-dialog")));Set(page.Find("#v2-page-modal-dialog"),"game ID","local");
        await using(var external=fixture.CreateDbContext()){var row=await external.CheckLocations.SingleAsync(x=>x.Id==check.Id);if(missing)external.Remove(row);else row.InGameId="external";await external.SaveChangesAsync();}
        page.FindAll("#v2-page-modal-dialog button").Single(x=>x.TextContent!.Trim()=="Apply").Click();page.WaitForAssertion(()=>{Assert.Empty(page.FindAll("#v2-page-modal-dialog"));Assert.Equal(2,loader.Loads);});
        Assert.Contains(context.JSInterop.Invocations,x=>x.Identifier=="focusEditorField"&&((string)x.Arguments[0]!).StartsWith(expectedPrefix,StringComparison.Ordinal));
    }
    [Fact]
    public async Task CheckMetadataRevertAnnotation_UsesRenderedControlAndPreservesAllOtherDraftMetadata()
    {
        await using var fixture=await MigratedSqliteFixture.CreateAsync();var(room,check)=await Seed(fixture);
        await using(var seed=fixture.CreateDbContext()){var row=await seed.CheckLocations.SingleAsync(x=>x.Id==check.Id);row.InGamePositionX=1;row.InGamePositionY=2;row.InGamePositionZ=3;row.LocalPositionX=4;row.LocalPositionY=5;row.LocalPositionZ=6;row.AnnotationSceneUnitX=7;row.AnnotationSceneUnitY=8;await seed.SaveChangesAsync();}
        using var context=Context(fixture,new RoomEditorV2CommandService(new LogicCatalogService(fixture)),out var loader);var page=context.RenderComponent<RoomEditorV2Page>(p=>p.Add(x=>x.RoomId,room.Id));
        page.Find($"#v2-check-metadata-{check.Id}").Click();page.WaitForAssertion(()=>Assert.NotNull(page.Find("#v2-page-modal-dialog")));var dialog=page.Find("#v2-page-modal-dialog");
        Assert.Empty(dialog.QuerySelectorAll("[aria-label='annotation X'], [aria-label='annotation Y']"));
        Set(dialog,"game ID","preserved-game");Set(dialog,"in-game X","11");Set(dialog,"in-game Y","12");Set(dialog,"in-game Z","13");Set(dialog,"local X","14");Set(dialog,"local Y","15");Set(dialog,"local Z","16");Set(dialog,"annotation scene X","17");Set(dialog,"annotation scene Y","18");
        dialog.QuerySelectorAll("button").Single(x=>x.TextContent!.Trim()=="revert annotation to game position").Click();
        dialog.QuerySelectorAll("button").Single(x=>x.TextContent!.Trim()=="Apply").Click();page.WaitForAssertion(()=>{Assert.Empty(page.FindAll("#v2-page-modal-dialog"));Assert.Equal(2,loader.Loads);});
        await using var verify=fixture.CreateDbContext();var saved=await verify.CheckLocations.SingleAsync(x=>x.Id==check.Id);
        Assert.Equal(("preserved-game",11d,12d,13d,14d,15d,16d),(saved.InGameId,saved.InGamePositionX,saved.InGamePositionY,saved.InGamePositionZ,saved.LocalPositionX,saved.LocalPositionY,saved.LocalPositionZ));Assert.Null(saved.AnnotationSceneUnitX);Assert.Null(saved.AnnotationSceneUnitY);
    }
    private static TestContext Context(MigratedSqliteFixture fixture, IRoomEditorV2CommandService commands, out CountingLoader loader){var c=new TestContext();c.JSInterop.Mode=JSRuntimeMode.Loose;c.JSInterop.SetupVoid("focusV2ModalDialog",_=>true);c.JSInterop.SetupVoid("focusEditorField",_=>true);loader=new(new RoomEditorV2LogicLoader(fixture));c.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);c.Services.AddSingleton(commands);return c;}
    private static async Task<(Room,CheckLocation)> Seed(MigratedSqliteFixture f){var room=new Room{FriendlyName="room",ReferenceId="room",SortOrder=0};var check=new CheckLocation{RoomId=room.Id,FriendlyName="check",Requirements="r",SortOrder=0,InGameId="duplicate"};var transition=new RoomTransition{RoomId=room.Id,Alias="a",FriendlyName="exit",Requirements="r",SortOrder=0,InGameId="duplicate"};await using var db=f.CreateDbContext();db.AddRange(room,check,transition);await db.SaveChangesAsync();return(room,check);}
    private static void Set(IElement dialog,string label,string value)=>dialog.QuerySelector($"[aria-label='{label}']")!.Input(value);
    private sealed class CountingLoader(IRoomEditorV2LogicLoader inner):IRoomEditorV2LogicLoader{public int Loads{get;private set;}public async Task<RoomEditorV2View?> LoadAsync(Guid id,CancellationToken token){Loads++;return await inner.LoadAsync(id,token);}}
    private sealed class HeldCommands(IRoomEditorV2CommandService inner):IRoomEditorV2CommandService{public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public async Task<V2CheckCommandOutcome> SaveCheckMetadataAsync(Guid room,CheckMetadataDurableBaseline baseline,CheckInGameMetadataDraft draft){Entered.TrySetResult();await Release.Task;return await inner.SaveCheckMetadataAsync(room,baseline,draft);}public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid r,SubroomDurableBaseline b,SubroomDraft d)=>inner.SaveSubroomAsync(r,b,d);public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid r,SubroomDraft d)=>inner.CreateSubroomAsync(r,d);public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid r,Guid id,int i)=>inner.ReorderSubroomAsync(r,id,i);public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid r,Guid id,bool a)=>inner.SetSubroomArchiveAsync(r,id,a);public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid r,Guid id)=>inner.DeleteSubroomAsync(r,id);}
}
