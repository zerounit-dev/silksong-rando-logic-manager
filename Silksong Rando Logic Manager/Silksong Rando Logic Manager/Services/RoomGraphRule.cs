using System.Text.Json;
using System.Text.Json.Serialization;

namespace Silksong_Rando_Logic_Manager.Services;

[JsonConverter(typeof(RoomGraphRuleWriter))]
public abstract record RoomGraphRule
{
    public string? Note { get; init; }
    public string? Explanation { get; init; }
}

internal sealed record RoomGraphRuleAtom(string Atom) : RoomGraphRule;
internal enum RoomGraphRuleGroupKind { And, Or, Group }
internal sealed record RoomGraphRuleGroup(RoomGraphRuleGroupKind Kind, IReadOnlyList<RoomGraphRule> Children) : RoomGraphRule;

internal sealed class RoomGraphRuleWriter : JsonConverter<RoomGraphRule>
{
    public override RoomGraphRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The room graph is an export-only format.");

    public override void Write(Utf8JsonWriter writer, RoomGraphRule value, JsonSerializerOptions options)
    {
        // Keep nested rule output on the same writer (indentation/escaping)
        // without building a matching-depth CLR serializer call stack.
        var pending = new Stack<(RoomGraphRuleGroup Group, int NextChild)>();
        WriteStart(value);
        while (pending.TryPop(out var frame))
        {
            var (group, nextChild) = frame;
            if (nextChild == group.Children.Count)
            {
                writer.WriteEndArray();
                WriteEnd(group);
                continue;
            }
            pending.Push((group, nextChild + 1));
            WriteStart(group.Children[nextChild]);
        }

        void WriteStart(RoomGraphRule rule)
        {
            writer.WriteStartObject();
            switch (rule)
            {
                case RoomGraphRuleAtom atom:
                    writer.WriteString("kind", "atom");
                    writer.WriteString("atom", atom.Atom);
                    WriteEnd(atom);
                    break;
                case RoomGraphRuleGroup group:
                    writer.WriteString("kind", group.Kind switch
                    {
                        RoomGraphRuleGroupKind.And => "and",
                        RoomGraphRuleGroupKind.Or => "or",
                        RoomGraphRuleGroupKind.Group => "group",
                        _ => throw new ArgumentOutOfRangeException(nameof(value))
                    });
                    writer.WriteStartArray("children");
                    pending.Push((group, 0));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        void WriteEnd(RoomGraphRule rule)
        {
            if (rule.Note is not null) writer.WriteString("note", rule.Note);
            if (rule.Explanation is not null) writer.WriteString("explanation", rule.Explanation);
            writer.WriteEndObject();
        }
    }
}
