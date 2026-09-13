using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneDumpParser
{
    public const long MaximumBytes = 256L * 1024 * 1024;

    private readonly List<SceneDumpWarning> warnings = [];
    private SceneJsonReader json = null!;

    public Task<SceneDumpReview> ParseAsync(Stream stream, CancellationToken cancellationToken = default) =>
        Task.Run(() => Parse(new AsyncReadStream(stream)), cancellationToken);

    public SceneDumpReview Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > MaximumBytes)
        {
            throw new SceneDumpTooLargeException(MaximumBytes);
        }

        warnings.Clear();

        using var limited = new ByteLimitedStream(stream, MaximumBytes);
        using var text = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        json = new SceneJsonReader(text);

        json.Expect('{');
        string sceneName = string.Empty;
        string? dumpStage = null;
        SceneMapContextEvidence? mapContext = null;
        var roots = new List<SceneDumpObject>();
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "name":
                    sceneName = json.ReadNullableString() ?? string.Empty;
                    break;
                case "dumpStage":
                    dumpStage = json.ReadNullableString();
                    break;
                case "rootObjects":
                    roots = ParseObjects("rootObjects");
                    break;
                case "mapContext":
                    mapContext = ParseMapContext();
                    break;
                default:
                    json.SkipValue();
                    break;
            }

            json.TryConsume(',');
        }

        json.EnsureEnd();
        for (var index = 0; index < roots.Count; index++)
        {
            AssignContext(roots[index], null, 0, index.ToString(CultureInfo.InvariantCulture));
        }

        return new SceneDumpReview(sceneName, roots, warnings.ToArray(), VerifiedSceneUnitSize(sceneName, dumpStage, mapContext));
    }

    private SceneMapContextEvidence? ParseMapContext()
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return null;
        }

        var evidence = new SceneMapContextEvidence();
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "metadata": ParseMapMetadata(evidence); break;
                case "gameMapCurrentSceneSize": ParseMapCurrentSceneSize(evidence); break;
                case "gameMapCurrentSceneSizeValid": evidence.CurrentSizeValid = json.ReadNullableBoolean(); break;
                case "mapMatchStatus": evidence.MapMatchStatus = json.ReadNullableString(); break;
                case "selectedMapSceneName": evidence.SelectedMapSceneName = json.ReadNullableString(); break;
                case "selectedMapScene": ParseSelectedMapScene(evidence); break;
                default: json.SkipValue(); break;
            }

            json.TryConsume(',');
        }

        return evidence;
    }

    private void ParseMapMetadata(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "activeSceneName") evidence.ActiveSceneName = json.ReadNullableString();
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParseMapCurrentSceneSize(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "x") evidence.CurrentWidth = json.ReadNullableDouble();
            else if (property == "y") evidence.CurrentHeight = json.ReadNullableDouble();
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParseSelectedMapScene(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "sceneSize") ParseSelectedSceneSize(evidence);
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParseSelectedSceneSize(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "preferred") ParsePreferredSceneSize(evidence);
            else json.SkipValue();
            json.TryConsume(',');
        }
    }

    private void ParsePreferredSceneSize(SceneMapContextEvidence evidence)
    {
        if (!json.TryConsume('{'))
        {
            json.SkipValue();
            return;
        }

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "sceneName": evidence.PreferredSceneName = json.ReadNullableString(); break;
                case "width": evidence.PreferredWidth = json.ReadNullableDouble(); break;
                case "height": evidence.PreferredHeight = json.ReadNullableDouble(); break;
                case "authoritativeForGameplayConversion": evidence.PreferredAuthoritative = json.ReadNullableBoolean(); break;
                default: json.SkipValue(); break;
            }

            json.TryConsume(',');
        }
    }

    private static SceneUnitSize? VerifiedSceneUnitSize(string sceneName, string? dumpStage, SceneMapContextEvidence? evidence)
    {
        if (evidence is not { CurrentWidth: { } width, CurrentHeight: { } height, PreferredWidth: { } preferredWidth, PreferredHeight: { } preferredHeight } ||
            !string.Equals(dumpStage, "sceneReady", StringComparison.Ordinal) ||
            !string.Equals(sceneName, evidence.ActiveSceneName, StringComparison.Ordinal) ||
            !string.Equals(sceneName, evidence.SelectedMapSceneName, StringComparison.Ordinal) ||
            !string.Equals(sceneName, evidence.PreferredSceneName, StringComparison.Ordinal) ||
            evidence.CurrentSizeValid is not true ||
            !string.Equals(evidence.MapMatchStatus, "unambiguous", StringComparison.Ordinal) ||
            evidence.PreferredAuthoritative is not true ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            width != preferredWidth || height != preferredHeight)
        {
            return null;
        }

        return new SceneUnitSize(width, height);
    }

    private List<SceneDumpObject> ParseObjects(string context)
    {
        var objects = new List<SceneDumpObject>();
        if (!json.TryConsume('['))
        {
            Warn(context, "Expected an array.");
            json.SkipValue();
            return objects;
        }

        var index = 0;
        while (!json.TryConsume(']'))
        {
            if (json.Peek() != '{')
            {
                Warn($"{context}[{index}]", "Expected an object.");
                json.SkipValue();
            }
            else
            {
                objects.Add(ParseObject($"{context}[{index}]"));
            }

            index++;
            json.TryConsume(',');
        }

        return objects;
    }

    private SceneDumpObject ParseObject(string context)
    {
        json.Expect('{');
        var name = string.Empty;
        string? tag = null;
        int? layer = null;
        bool? activeSelf = null;
        bool? activeInHierarchy = null;
        ScenePosition? worldPosition = null;
        ScenePosition? localPosition = null;
        var components = new List<SceneDumpComponent>();
        var children = new List<SceneDumpObject>();

        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "name":
                    name = json.ReadNullableString() ?? string.Empty;
                    break;
                case "tag":
                    tag = json.ReadNullableString();
                    break;
                case "layer":
                    layer = json.ReadNullableInt();
                    break;
                case "activeSelf":
                    activeSelf = json.ReadNullableBoolean();
                    break;
                case "activeInHierarchy":
                    activeInHierarchy = json.ReadNullableBoolean();
                    break;
                case "transform":
                    (worldPosition, localPosition) = ParseTransform($"{context}.transform");
                    break;
                case "components":
                    components = ParseComponents($"{context}.components");
                    break;
                case "children":
                    children = ParseObjects($"{context}.children");
                    break;
                default:
                    json.SkipValue();
                    break;
            }

            json.TryConsume(',');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Warn(context, "Object name is missing.");
        }

        if (worldPosition is null && localPosition is null)
        {
            Warn(context, "Transform position data is missing.");
        }

        return new SceneDumpObject(name, tag, layer, activeSelf, activeInHierarchy, worldPosition, localPosition, components, children);
    }

    private (ScenePosition? World, ScenePosition? Local) ParseTransform(string context)
    {
        if (!json.TryConsume('{'))
        {
            Warn(context, "Expected an object.");
            json.SkipValue();
            return (null, null);
        }

        ScenePosition? world = null;
        ScenePosition? local = null;
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "position")
            {
                world = ParsePosition($"{context}.position");
            }
            else if (property == "localPosition")
            {
                local = ParsePosition($"{context}.localPosition");
            }
            else
            {
                json.SkipValue();
            }

            json.TryConsume(',');
        }

        return (world, local);
    }

    private ScenePosition? ParsePosition(string context)
    {
        if (!json.TryConsume('{'))
        {
            Warn(context, "Expected an object.");
            json.SkipValue();
            return null;
        }

        double? x = null;
        double? y = null;
        double? z = null;
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            switch (property)
            {
                case "x": x = json.ReadNullableDouble(); break;
                case "y": y = json.ReadNullableDouble(); break;
                case "z": z = json.ReadNullableDouble(); break;
                default: json.SkipValue(); break;
            }

            json.TryConsume(',');
        }

        if (x is null && y is null && z is null)
        {
            Warn(context, "Coordinate values are missing.");
        }

        return new ScenePosition(x, y, z);
    }

    private List<SceneDumpComponent> ParseComponents(string context)
    {
        var components = new List<SceneDumpComponent>();
        if (!json.TryConsume('['))
        {
            Warn(context, "Expected an array.");
            json.SkipValue();
            return components;
        }

        var index = 0;
        while (!json.TryConsume(']'))
        {
            if (json.Peek() != '{')
            {
                Warn($"{context}[{index}]", "Expected an object.");
                json.SkipValue();
            }
            else
            {
                components.Add(ParseComponent($"{context}[{index}]"));
            }

            index++;
            json.TryConsume(',');
        }

        return components;
    }

    private SceneDumpComponent ParseComponent(string context)
    {
        json.Expect('{');
        var type = string.Empty;
        var evidence = new SceneComponentEvidence();
        while (!json.TryConsume('}'))
        {
            var property = json.ReadPropertyName();
            json.Expect(':');
            if (property == "type")
            {
                type = json.ReadNullableString() ?? string.Empty;
            }
            else if (property == "data")
            {
                ScanComponentData(evidence, false, true, $"{context}.data");
            }
            else
            {
                json.SkipValue();
            }

            json.TryConsume(',');
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            Warn(context, "Component type is missing.");
        }

        return new SceneDumpComponent(
            type,
            type.StartsWith("Persistent", StringComparison.Ordinal) ? evidence.ItemDataId : null,
            evidence.TargetScene,
            evidence.EntryPoint);
    }

    private void ScanComponentData(SceneComponentEvidence evidence, bool insideItemData, bool isComponentDataRoot, string context)
    {
        if (json.TryConsume('{'))
        {
            while (!json.TryConsume('}'))
            {
                var property = json.ReadPropertyName();
                json.Expect(':');
                if (property == "targetScene")
                {
                    evidence.TargetScene ??= json.ReadNullableString();
                }
                else if (property == "entryPoint")
                {
                    evidence.EntryPoint ??= json.ReadNullableString();
                }
                else if (insideItemData && property == "ID")
                {
                    evidence.ItemDataId ??= json.ReadNullableString();
                }
                else
                {
                    ScanComponentData(evidence, insideItemData || property == "itemData", false, $"{context}.{property}");
                }

                json.TryConsume(',');
            }

            return;
        }

        if (json.TryConsume('['))
        {
            while (!json.TryConsume(']'))
            {
                ScanComponentData(evidence, insideItemData, false, context);
                json.TryConsume(',');
            }

            return;
        }

        if (isComponentDataRoot && json.Peek() != 'n')
        {
            Warn(context, "Component data is not an object, array, or null.");
        }

        json.SkipValue();
    }

    private void AssignContext(SceneDumpObject node, Guid? parentId, int depth, string path)
    {
        node.ParentId = parentId;
        node.Depth = depth;
        node.HierarchyPath = path;
        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = node.Children[index];
            AssignContext(child, node.Id, depth + 1, $"{path}/{child.Name}[{index}]");
        }
    }

    private void Warn(string path, string message) => warnings.Add(new SceneDumpWarning(path, message));

    private sealed class SceneComponentEvidence
    {
        public string? ItemDataId { get; set; }
        public string? TargetScene { get; set; }
        public string? EntryPoint { get; set; }
    }

    private sealed class SceneMapContextEvidence
    {
        public string? ActiveSceneName { get; set; }
        public double? CurrentWidth { get; set; }
        public double? CurrentHeight { get; set; }
        public bool? CurrentSizeValid { get; set; }
        public string? MapMatchStatus { get; set; }
        public string? SelectedMapSceneName { get; set; }
        public string? PreferredSceneName { get; set; }
        public double? PreferredWidth { get; set; }
        public double? PreferredHeight { get; set; }
        public bool? PreferredAuthoritative { get; set; }
    }
}

public sealed class SceneDumpReview(string sceneName, IReadOnlyList<SceneDumpObject> rootObjects, IReadOnlyList<SceneDumpWarning> warnings, SceneUnitSize? sceneUnitSize = null)
{
    public string SceneName { get; } = sceneName;
    public IReadOnlyList<SceneDumpObject> RootObjects { get; } = rootObjects;
    public IReadOnlyList<SceneDumpWarning> Warnings { get; } = warnings;
    public SceneUnitSize? SceneUnitSize { get; } = sceneUnitSize;
}

public sealed record SceneUnitSize(double Width, double Height);

public sealed class SceneDumpObject(
    string name,
    string? tag,
    int? layer,
    bool? activeSelf,
    bool? activeInHierarchy,
    ScenePosition? worldPosition,
    ScenePosition? localPosition,
    IReadOnlyList<SceneDumpComponent> components,
    IReadOnlyList<SceneDumpObject> children)
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid? ParentId { get; internal set; }
    public int Depth { get; internal set; }
    public string HierarchyPath { get; internal set; } = string.Empty;
    public string Name { get; } = name;
    public string? Tag { get; } = tag;
    public int? Layer { get; } = layer;
    public bool? ActiveSelf { get; } = activeSelf;
    public bool? ActiveInHierarchy { get; } = activeInHierarchy;
    public ScenePosition? WorldPosition { get; } = worldPosition;
    public ScenePosition? LocalPosition { get; } = localPosition;
    public IReadOnlyList<SceneDumpComponent> Components { get; } = components;
    public IReadOnlyList<SceneDumpObject> Children { get; } = children;
    public List<string> CandidateReasons { get; } = [];
    public SceneDumpClassification Classification { get; set; }
}

public sealed record SceneDumpComponent(string Type, string? ItemDataId, string? TargetScene, string? EntryPoint);

public sealed record ScenePosition(double? X, double? Y, double? Z);

public sealed record SceneDumpWarning(string HierarchyPath, string Message);

public enum SceneDumpClassification
{
    Other,
    Exit,
    Check
}

public sealed class SceneDumpParseException(string message) : Exception(message);

public sealed class SceneDumpTooLargeException(long maximumBytes) : Exception($"Scene dump exceeds the {maximumBytes / (1024 * 1024)} MiB limit.");

internal sealed class SceneJsonReader(TextReader reader)
{
    public int Peek()
    {
        SkipWhitespace();
        return reader.Peek();
    }

    public bool TryConsume(char expected)
    {
        if (Peek() != expected)
        {
            return false;
        }

        reader.Read();
        return true;
    }

    public void Expect(char expected)
    {
        if (!TryConsume(expected))
        {
            throw Error($"Expected '{expected}'.");
        }
    }

    public string ReadPropertyName()
    {
        if (Peek() != '"')
        {
            throw Error("Expected a property name.");
        }

        return ReadString();
    }

    public string? ReadNullableString()
    {
        if (Peek() == '"')
        {
            return ReadString();
        }

        if (TryReadLiteral("null"))
        {
            return null;
        }

        SkipValue();
        return null;
    }

    public int? ReadNullableInt()
    {
        var number = ReadNullableDouble();
        return number is >= int.MinValue and <= int.MaxValue && Math.Truncate(number.Value) == number.Value ? (int)number.Value : null;
    }

    public double? ReadNullableDouble()
    {
        var first = Peek();
        if (first == 'n')
        {
            TryReadLiteral("null");
            return null;
        }

        if (first is not ('-' or >= '0' and <= '9'))
        {
            SkipValue();
            return null;
        }

        var token = new StringBuilder();
        while (reader.Peek() is var next && next >= 0 && "-+0123456789.eE".Contains((char)next))
        {
            token.Append((char)reader.Read());
        }

        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public bool? ReadNullableBoolean()
    {
        if (TryReadLiteral("true")) return true;
        if (TryReadLiteral("false")) return false;
        if (TryReadLiteral("null")) return null;
        SkipValue();
        return null;
    }

    public void SkipValue()
    {
        switch (Peek())
        {
            case '"':
                _ = ReadString();
                return;
            case '{':
                reader.Read();
                while (!TryConsume('}'))
                {
                    _ = ReadPropertyName();
                    Expect(':');
                    SkipValue();
                    TryConsume(',');
                }
                return;
            case '[':
                reader.Read();
                while (!TryConsume(']'))
                {
                    SkipValue();
                    TryConsume(',');
                }
                return;
            case -1:
                throw Error("Unexpected end of document.");
            default:
                while (reader.Peek() is var next && next >= 0 && !char.IsWhiteSpace((char)next) && !",]}".Contains((char)next))
                {
                    reader.Read();
                }
                return;
        }
    }

    public void EnsureEnd()
    {
        if (Peek() != -1)
        {
            throw Error("Unexpected content after the scene dump.");
        }
    }

    private string ReadString()
    {
        Expect('"');
        var value = new StringBuilder();
        while (true)
        {
            var next = reader.Read();
            if (next < 0) throw Error("Unterminated string.");
            if (next == '"') return value.ToString();
            if (next != '\\')
            {
                value.Append((char)next);
                continue;
            }

            var escape = reader.Read();
            value.Append(escape switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => ReadUnicodeCharacter(),
                _ => throw Error("Invalid string escape.")
            });
        }
    }

    private char ReadUnicodeCharacter()
    {
        Span<char> hex = stackalloc char[4];
        for (var index = 0; index < hex.Length; index++)
        {
            var next = reader.Read();
            if (next < 0) throw Error("Incomplete unicode escape.");
            hex[index] = (char)next;
        }

        return ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? (char)value
            : throw Error("Invalid unicode escape.");
    }

    private bool TryReadLiteral(string literal)
    {
        if (Peek() != literal[0]) return false;
        foreach (var expected in literal)
        {
            if (reader.Read() != expected)
            {
                throw Error($"Invalid literal; expected '{literal}'.");
            }
        }

        return true;
    }

    private void SkipWhitespace()
    {
        while (reader.Peek() is var next && next >= 0 && char.IsWhiteSpace((char)next))
        {
            reader.Read();
        }
    }

    private static SceneDumpParseException Error(string message) => new(message);
}

internal sealed class ByteLimitedStream(Stream inner, long maximumBytes) : Stream
{
    private long bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => bytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (bytesRead >= maximumBytes)
        {
            if (inner.ReadByte() >= 0) throw new SceneDumpTooLargeException(maximumBytes);
            return 0;
        }

        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, maximumBytes - bytesRead)]);
        bytesRead += read;
        return read;
    }

    public override int ReadByte()
    {
        if (bytesRead >= maximumBytes)
        {
            if (inner.ReadByte() >= 0) throw new SceneDumpTooLargeException(maximumBytes);
            return -1;
        }

        var value = inner.ReadByte();
        if (value >= 0) bytesRead++;
        return value;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class AsyncReadStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override int Read(Span<byte> buffer)
    {
        var copy = new byte[buffer.Length];
        var read = inner.ReadAsync(copy).AsTask().GetAwaiter().GetResult();
        copy.AsSpan(0, read).CopyTo(buffer);
        return read;
    }
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
