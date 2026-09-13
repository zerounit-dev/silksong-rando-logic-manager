using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapRenderProjectionServiceTests
{
    private readonly MapRenderProjectionService service = new();

    [Fact]
    public void Project_MergesAdjacentLinkedChunksWithoutAnInternalBoundary()
    {
        var roomId = Guid.NewGuid();

        var owners = Project([Chunk("a", roomId, 0, 0, 1, 1), Chunk("b", roomId, 1, 0, 2, 1)]);

        var owner = Assert.Single(owners);
        Assert.Equal(roomId, owner.ActiveResolvedRoomId);
        Assert.Equal("M 0 0 L 2 0 L 2 1 L 0 1 Z", owner.SvgPathData);
    }

    [Fact]
    public void Project_PreservesDisconnectedLinkedIslandsUnderOneOwner()
    {
        var roomId = Guid.NewGuid();

        var owners = Project([Chunk("a", roomId, 0, 0, 1, 1), Chunk("b", roomId, 3, 0, 4, 1)]);

        var owner = Assert.Single(owners);
        Assert.Equal(roomId, owner.ActiveResolvedRoomId);
        Assert.Equal(2, owner.SvgPathData.Count(character => character == 'M'));
    }

    [Fact]
    public void Project_PreservesAnExposedHoleBoundary()
    {
        var roomId = Guid.NewGuid();
        var chunks = Enumerable.Range(0, 3)
            .SelectMany(x => Enumerable.Range(0, 3)
                .Where(y => x != 1 || y != 1)
                .Select(y => Chunk($"{x}-{y}", roomId, x, y, x + 1, y + 1)));

        var owner = Assert.Single(Project(chunks));

        Assert.Equal(2, owner.SvgPathData.Count(character => character == 'M'));
    }

    [Fact]
    public void Project_UnlinkedChunkWinsOverLinkedChunkRegardlessOfZ()
    {
        var roomId = Guid.NewGuid();
        var unlinkedId = Guid.NewGuid();

        var owners = Project([
            Chunk("linked", roomId, 0, 0, 2, 2, 0),
            Chunk("unlinked", null, 1, 1, 3, 3, 100, unlinkedId)]);

        Assert.Equal(2, owners.Count);
        var unlinked = Assert.Single(owners, owner => owner.UnlinkedChunkId == unlinkedId);
        Assert.Equal("M 1 1 L 3 1 L 3 3 L 1 3 Z", unlinked.SvgPathData);
        var linked = Assert.Single(owners, owner => owner.ActiveResolvedRoomId == roomId);
        Assert.DoesNotContain("L 2 2", linked.SvgPathData);
    }

    [Fact]
    public void Project_UsesLowerZThenStableIdentityForOverlappingUnlinkedChunks()
    {
        var lowerZId = Guid.NewGuid();
        var tieWinnerId = Guid.NewGuid();

        var owners = Project([
            Chunk("z-high", null, 0, 0, 1, 1, 2),
            Chunk("z-low", null, 0, 0, 1, 1, 1, lowerZId),
            Chunk("tie-b", null, 2, 0, 3, 1, 1),
            Chunk("tie-a", null, 2, 0, 3, 1, 1, tieWinnerId)]);

        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, owner => owner.UnlinkedChunkId == lowerZId);
        Assert.Contains(owners, owner => owner.UnlinkedChunkId == tieWinnerId);
    }

    [Fact]
    public void Project_ComposesALargeAlignedGridWithoutInternalBoundaries()
    {
        var roomId = Guid.NewGuid();
        var chunks = Enumerable.Range(0, 64)
            .SelectMany(x => Enumerable.Range(0, 64).Select(y => Chunk($"{x:D2}-{y:D2}", roomId, x, y, x + 1, y + 1)))
            .ToList();

        var owner = Assert.Single(Project(chunks));

        Assert.Equal(roomId, owner.ActiveResolvedRoomId);
        Assert.Equal("M 0 0 L 64 0 L 64 64 L 0 64 Z", owner.SvgPathData);
    }

    [Fact]
    public void Project_RebuildsCachedProjectionWhenChunkGeometryChanges()
    {
        var roomId = Guid.NewGuid();
        var chunk = Chunk("a", roomId, 0, 0, 1, 1);

        var first = service.Project(new(Guid.Empty, [chunk]));
        var hit = service.Project(new(Guid.Empty, [chunk]));
        var changed = service.Project(new(Guid.Empty, [chunk with { MapUnitMaxX = 2 }]));
        var owner = Assert.Single(changed.Owners);

        Assert.False(first.CacheHit);
        Assert.True(hit.CacheHit);
        Assert.False(changed.CacheHit);
        Assert.Equal("M 0 0 L 2 0 L 2 1 L 0 1 Z", owner.SvgPathData);
    }

    private static MapRenderProjectionChunk Chunk(string identity, Guid? roomId, double minX, double minY, double maxX, double maxY, double? z = 0, Guid? chunkId = null) =>
        new(chunkId ?? Guid.NewGuid(), identity, roomId, minX, minY, maxX, maxY, z);
    private IReadOnlyList<MapRenderProjectionOwner> Project(IEnumerable<MapRenderProjectionChunk> chunks) =>
        service.Project(new(Guid.Empty, chunks.ToArray())).Owners;
}
