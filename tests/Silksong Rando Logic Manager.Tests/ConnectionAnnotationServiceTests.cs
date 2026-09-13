using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class ConnectionAnnotationServiceTests
{
    [Fact]
    public void GetEligibleGroups_ReturnsOneGroupForValidDirectedPair()
    {
        var roomId = Guid.NewGuid();
        var source = new Subroom { RoomId = roomId, ReferenceId = "source" };
        var destination = new Subroom { RoomId = roomId, ReferenceId = "destination" };
        var first = new SubroomConnection { Id = Guid.NewGuid(), RoomId = roomId, Alias = "A", FriendlyName = "Path", SourceSubroomReferenceText = "source", DestinationSubroomReferenceText = "destination" };
        var second = new SubroomConnection { Id = Guid.NewGuid(), RoomId = roomId, Alias = " a ", FriendlyName = "Path", SourceSubroomReferenceText = "destination", DestinationSubroomReferenceText = "source" };

        var group = Assert.Single(new ConnectionAnnotationService().GetEligibleGroups([first, second], [source, destination]));

        Assert.Equal("A", group.Alias);
        Assert.Equal(2, group.Rows.Count);
    }

    [Fact]
    public void GetEligibleGroups_ExcludesMalformedAliasesAndPathways()
    {
        var roomId = Guid.NewGuid();
        var subroom = new Subroom { RoomId = roomId, ReferenceId = "subroom" };
        var invalid = new SubroomConnection { RoomId = roomId, Alias = "long", FriendlyName = "Path", SourceSubroomReferenceText = "subroom", DestinationSubroomReferenceText = "subroom" };

        Assert.Empty(new ConnectionAnnotationService().GetEligibleGroups([invalid], [subroom]));
    }
}
