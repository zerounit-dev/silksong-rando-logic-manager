using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Client;

/// <summary>Transport structure only: never reinterpret an authored gate or reject duplicate aliases/edges.</summary>
internal static class AuditSnapshotReader
{
    private static void Require([DoesNotReturnIf(false)] bool valid) { if (!valid) throw new JsonException("Unusable audit snapshot."); }
    private static ImmutableArray<T> Copy<T>(IReadOnlyList<T>? source, Func<T, bool> valid)
    {
        Require(source is not null);
        Require(source!.All(x => x is not null && valid(x)));
        return source.ToImmutableArray();
    }
    private static ImmutableArray<T> Identified<T>(IReadOnlyList<T>? source, Func<T, Guid> id, Func<T, bool> valid)
    {
        var values = Copy(source, valid);
        Require(values.All(x => id(x) != Guid.Empty) && values.Select(id).Distinct().Count() == values.Length);
        return values;
    }
    public static RoomAuditSnapshot Freeze(RoomAuditSnapshot value, Guid roomId)
    {
        Require(value.RoomId == roomId && value.Name is not null);
        var subrooms = Identified(value.Subrooms, x => x.Id, x => x.Name is not null);
        var checks = Identified(value.Checks, x => x.Id, x => x.RoomId != Guid.Empty && x.Name is not null && x.RoomName is not null);
        var predicates = Identified(value.Predicates, x => x.Id, x => x.Name is not null && x.InputSyntax is not null && x.OutputSyntax is not null &&
            (x.InputShape is null || Enum.IsDefined(x.InputShape.Value)));
        var rows = Copy(value.Rows, x => x.Id != Guid.Empty && x.Kind is "exit" or "connection" or "check" && x.Name is not null && x.Requirement is not null);
        Require(rows.Select(x => (x.Kind, x.Id)).Distinct().Count() == rows.Length);
        rows = rows.Select(x => x with { Requirement = FreezeRequirement(x.Requirement) }).ToImmutableArray();
        var scene = value.Scene;
        if (scene is not null)
        {
            var frames = Identified(scene.Frames, x => x.EntityId, x => x.Label is not null && Finite(x.X, x.Y, x.Width, x.Height));
            var markers = Copy(scene.Markers, x => x.EntityId != Guid.Empty && x.Kind is not null && x.Label is not null && Finite(x.X, x.Y));
            Require(markers.Select(x => (x.Kind, x.EntityId)).Distinct().Count() == markers.Length);
            var threads = scene.Threads is null ? null : (IReadOnlyList<Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.SceneRelationshipThreadView>)Copy(scene.Threads,
                x => x.MarkerEntityId != Guid.Empty && x.FrameEntityId != Guid.Empty && Finite(x.MarkerX, x.MarkerY, x.FrameX, x.FrameY, x.FrameWidth, x.FrameHeight));
            Require(Finite(scene.SceneMinX, scene.SceneMinY) && (scene.SceneMaxX is null || double.IsFinite(scene.SceneMaxX.Value)) &&
                (scene.SceneMaxY is null || double.IsFinite(scene.SceneMaxY.Value)) &&
                (!scene.HasValidBounds || SceneBounds.TryGetSize(scene.SceneMinX, scene.SceneMinY, scene.SceneMaxX, scene.SceneMaxY, out _, out _)));
            Require(scene.Image is null || Enum.IsDefined(scene.Image.BoundsImageSeverity));
            scene = scene with { Frames = frames, Markers = markers, Threads = threads };
        }
        return value with { Subrooms = subrooms, Checks = checks, Rows = rows, Predicates = predicates,
            Items = Values(value.Items), Crests = Values(value.Crests), Methods = Values(value.Methods), Souls = Values(value.Souls), Countables = Values(value.Countables), Scene = scene };
    }
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static ImmutableArray<AuditValue> Values(IReadOnlyList<AuditValue> values) => Identified(values, x => x.Id, x => x.Name is not null && x.OutputValue is not null);
    private static AuditRequirement FreezeRequirement(AuditRequirement value)
    {
        Require(value.Source is not null);
        return value with { Reasons = Copy(value.Reasons, x => x is not null), Expression = value.Expression is null ? null : FreezeExpression(value.Expression, value.Source.Length) };
    }
    private static AuditExpression FreezeExpression(AuditExpression expression, int sourceLength) => expression switch
    {
        AuditBoolean boolean when boolean.Left is not null && boolean.Right is not null => boolean with
        { Left = FreezeExpression(boolean.Left, sourceLength), Right = FreezeExpression(boolean.Right, sourceLength) },
        AuditAtom atom => FreezeAtom(atom, sourceLength),
        _ => throw new JsonException("Unusable audit expression.")
    };
    private static AuditAtom FreezeAtom(AuditAtom atom, int sourceLength)
    {
        Require(atom.PredicateId != Guid.Empty && atom.Signature is not null && atom.Arguments is not null &&
            atom.Start >= 0 && atom.Length >= 0 && atom.Start <= sourceLength && atom.Length <= sourceLength - atom.Start);
        return atom with { Operands = Copy(atom.Operands, x => x.Role is not null && x.Value is not null && x.Source is not null) };
    }
}
