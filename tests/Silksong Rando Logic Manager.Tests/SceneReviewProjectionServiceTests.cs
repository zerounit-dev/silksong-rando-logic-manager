using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SceneReviewProjectionServiceTests
{
    private readonly SceneReviewProjectionService service = new();

    [Fact]
    public void Project_UsesOnlyVisibleFiniteWorldPositionsAndMarksTheHoveredPoint()
    {
        var visible = new SceneDumpObject("Visible", null, null, null, null, new ScenePosition(3, 4, 0), null, [], []);
        var hidden = new SceneDumpObject("Hidden", null, null, null, null, new ScenePosition(8, 9, 0), null, [], []);
        var invalid = new SceneDumpObject("Invalid", null, null, null, null, new ScenePosition(double.NaN, 2, 0), null, [], []);

        var points = service.CreatePoints([visible, hidden, invalid]);
        var projection = service.Project(points, new HashSet<Guid> { visible.Id, invalid.Id }, hidden.Id);

        Assert.Equal([new SceneReviewPoint(visible.Id, 3, -4)], projection.Points);
        Assert.Equal(new SceneReviewPoint(hidden.Id, 8, -9), projection.HoveredPoint);
        Assert.True(projection.Bounds.Width > 0);
        Assert.True(projection.Bounds.Height > 0);
    }

    [Fact]
    public void CreateDraft_DefaultsOnlyUniqueSameTypeTargetRoomMatchesToUpdate()
    {
        var roomId = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var node = new SceneDumpObject("Exit", null, null, null, null, null, null, [new SceneDumpComponent("TransitionPoint", null, null, null)], []);
        node.Classification = SceneDumpClassification.Exit;
        var preview = new SceneImportPreview([], [new SceneImportObjectMatch(node.Id, SceneDumpClassification.Exit, "Exit", [new SceneImportRecordMatch(transitionId, roomId, SceneDumpClassification.Exit)])], [new SceneImportSelectableRecord(transitionId, roomId, SceneDumpClassification.Exit, "Exit", "Exit"), new SceneImportSelectableRecord(Guid.NewGuid(), Guid.NewGuid(), SceneDumpClassification.Exit, "Other room", "Exit")], [new SceneImportSelectableRecord(Guid.NewGuid(), roomId, SceneDumpClassification.Check, "Wrong type", "Exit")]);

        var matching = service.CreateDraft(node, preview, roomId);
        var otherRoom = service.CreateDraft(node, preview, Guid.NewGuid());

        Assert.Equal(SceneImportMode.Update, matching.Mode);
        Assert.Equal(transitionId, matching.ExistingRecordId);
        Assert.Equal("Exit", matching.FriendlyName);
        Assert.Equal(SceneImportMode.Create, otherRoom.Mode);
        Assert.Null(otherRoom.ExistingRecordId);
        Assert.Equal([transitionId], service.SelectableRecords(preview, SceneDumpClassification.Exit, roomId).Select(record => record.Id));
    }
}
