using System.Text;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SceneDumpParserTests
{
    [Fact]
    public void Parse_PreservesNestedHierarchyCoordinatesAndComponentEvidence()
    {
        var review = Parse("""
            {
              "name": "Dock_01",
              "rootObjects": [
                {
                  "name": "Exit",
                  "tag": "Exit",
                  "layer": 7,
                  "activeSelf": true,
                  "activeInHierarchy": true,
                  "transform": {
                    "position": { "x": 1.5, "y": 2, "z": 3 },
                    "localPosition": { "x": 4, "y": 5, "z": 6 }
                  },
                  "components": [
                    { "type": "TransitionPoint", "data": { "targetScene": "Dock_02", "entryPoint": "in" } },
                    { "type": "PersistentItem", "data": { "itemData": { "ID": "check-id", "instanceId": 99 } } }
                  ],
                  "children": [
                    {
                      "name": "Exit",
                      "activeSelf": false,
                      "activeInHierarchy": false,
                      "transform": { "position": { "x": 7, "y": 8, "z": 9 } },
                      "components": [],
                      "children": []
                    }
                  ]
                }
              ]
            }
            """);

        var root = Assert.Single(review.RootObjects);
        var child = Assert.Single(root.Children);
        var transition = root.Components[0];
        var persistent = root.Components[1];

        Assert.Equal("Dock_01", review.SceneName);
        Assert.Equal("Exit", root.Name);
        Assert.Equal("Exit", child.Name);
        Assert.Equal(0, root.Depth);
        Assert.Equal(1, child.Depth);
        Assert.Equal(root.Id, child.ParentId);
        Assert.Equal("0/Exit[0]", child.HierarchyPath);
        Assert.False(child.ActiveInHierarchy);
        Assert.Equal(1.5, root.WorldPosition!.X);
        Assert.Equal(6, root.LocalPosition!.Z);
        Assert.Equal("Dock_02", transition.TargetScene);
        Assert.Equal("in", transition.EntryPoint);
        Assert.Equal("check-id", persistent.ItemDataId);
        Assert.Empty(review.Warnings);
    }

    [Fact]
    public void Parse_HandlesComponentDataShapesAndMalformedObjectsWithoutDroppingSiblings()
    {
        var review = Parse("""
            {
              "name": "Room",
              "rootObjects": [
                {
                  "name": "Malformed",
                  "transform": { "position": {} },
                  "components": {},
                  "children": [null]
                },
                {
                  "name": "Valid",
                  "transform": { "position": { "x": 1 } },
                  "components": [
                    { "type": "ArrayData", "data": [] },
                    { "type": "NullData", "data": null },
                    { "type": "ScalarData", "data": "unexpected" },
                    { "type": "PersistentThing", "data": { "itemData": { "ID": "" } } }
                  ],
                  "children": []
                },
                {
                  "name": "NoTransform",
                  "components": [],
                  "children": []
                }
              ]
            }
            """);

        Assert.Equal(3, review.RootObjects.Count);
        var valid = review.RootObjects[1];
        Assert.Equal("Valid", valid.Name);
        Assert.Equal(4, valid.Components.Count);
        Assert.Equal(string.Empty, valid.Components[3].ItemDataId);
        Assert.Contains(review.Warnings, warning => warning.Message == "Expected an array.");
        Assert.Contains(review.Warnings, warning => warning.Message == "Expected an object.");
        Assert.Contains(review.Warnings, warning => warning.Message == "Coordinate values are missing.");
        Assert.Contains(review.Warnings, warning => warning.Message == "Component data is not an object, array, or null.");
        Assert.Contains(review.Warnings, warning => warning.Message == "Transform position data is missing.");
    }

    [Fact]
    public void Parse_ExtractsOnlyVerifiedMapContextSceneDimensions()
    {
        var valid = Parse("""
            {
              "name": "Bellshrine",
              "dumpStage": "sceneReady",
              "mapContext": {
                "metadata": { "activeSceneName": "Bellshrine" },
                "gameMapCurrentSceneSize": { "x": 43, "y": 25 },
                "gameMapCurrentSceneSizeValid": true,
                "mapMatchStatus": "unambiguous",
                "selectedMapSceneName": "Bellshrine",
                "selectedMapScene": { "sceneSize": { "preferred": { "sceneName": "Bellshrine", "width": 43, "height": 25, "authoritativeForGameplayConversion": true } } }
              },
              "rootObjects": []
            }
            """);
        var additiveChild = Parse("""
            {
              "name": "Bellway_02_boss",
              "dumpStage": "sceneReady",
              "mapContext": {
                "metadata": { "activeSceneName": "Bellway_02" },
                "gameMapCurrentSceneSize": { "x": 120, "y": 50 },
                "gameMapCurrentSceneSizeValid": true,
                "mapMatchStatus": "unambiguous",
                "selectedMapSceneName": "Bellway_02",
                "selectedMapScene": { "sceneSize": { "preferred": { "sceneName": "Bellway_02", "width": 120, "height": 50, "authoritativeForGameplayConversion": true } } }
              },
              "rootObjects": []
            }
            """);

        Assert.Equal(new SceneUnitSize(43, 25), valid.SceneUnitSize);
        Assert.Empty(valid.Warnings);
        Assert.Null(additiveChild.SceneUnitSize);
    }

    [Fact]
    public void Parse_RejectsInvalidJson()
    {
        Assert.Throws<SceneDumpParseException>(() => Parse("{ \"name\": \"bad\", \"rootObjects\": ["));
    }

    [Fact]
    public void Parse_RejectsSeekableStreamsLargerThanTheLimit()
    {
        using var stream = new OversizedSeekableStream();

        Assert.Throws<SceneDumpTooLargeException>(() => new SceneDumpParser().Parse(stream));
    }

    [Fact]
    public async Task ParseAsync_ConsumesAnAsyncOnlyStream()
    {
        await using var stream = new AsyncOnlyStream(Encoding.UTF8.GetBytes("{ \"name\": \"Room\", \"rootObjects\": [] }"));

        var review = await new SceneDumpParser().ParseAsync(stream);

        Assert.Equal("Room", review.SceneName);
    }

    private static SceneDumpReview Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new SceneDumpParser().Parse(stream);
    }

    private sealed class OversizedSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => SceneDumpParser.MaximumBytes + 1;
        public override long Position { get; set; }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AsyncOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
        public override int Read(Span<byte> buffer) => throw new NotSupportedException("Use ReadAsync.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
