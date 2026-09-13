using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2ContractBoundaryTests
{
    private const string V2Namespace = "Silksong_Rando_Logic_Manager.Components.RoomEditorV2";

    [Fact]
    public void ProductionV2Contracts_ContainNoEntityOrDbContextReference()
    {
        var violations = RoomEditorV2ContractBoundary.FindViolations(GetProductionV2Types());

        Assert.Empty(violations);
    }

    [Fact]
    public void TypedAreaMapSurfaceAndV2PaneContainNoEfContract()
    {
        Assert.Empty(RoomEditorV2ContractBoundary.FindViolations([
            typeof(Silksong_Rando_Logic_Manager.Components.AreaMapSurface),
            typeof(Silksong_Rando_Logic_Manager.Components.RoomEditorV2.AreaMapPanePresentation),
            typeof(AreaMapSurfaceView), typeof(GlobalAreaMapGeometryView)]));
    }

    [Fact]
    public void ProductionV2SourceScope_ExistsForFutureContractDiscovery()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2");
        Assert.True(Directory.Exists(sourceRoot), $"Missing V2 source root: {sourceRoot}");

        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(sourceFiles);
        Assert.All(sourceFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)), path =>
            Assert.Contains($"namespace {V2Namespace}", File.ReadAllText(path), StringComparison.Ordinal));
        Assert.All(sourceFiles.Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)), path =>
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("@namespace ", source, StringComparison.Ordinal);
        });
    }

    [Theory]
    [MemberData(nameof(ProhibitedSurfaceFixtures))]
    public void BoundaryDetector_RejectsDirectAndWrappedProhibitedTypes(Type fixtureType, string expectedPathFragment)
    {
        var violations = RoomEditorV2ContractBoundary.FindViolations([fixtureType]);

        Assert.Contains(violations, violation => violation.Contains(expectedPathFragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigratedSqliteFixture_AppliesCurrentMigrationsAndCleansUpItsOwnedDatabase()
    {
        var fixture = await MigratedSqliteFixture.CreateAsync();
        var databasePath = fixture.DatabasePath;

        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await db.Database.CanConnectAsync());
            Assert.Equal(
                db.Database.GetMigrations(),
                await db.Database.GetAppliedMigrationsAsync());
        }

        await fixture.DisposeAsync();
        Assert.False(File.Exists(databasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)!));
    }

    public static IEnumerable<object[]> ProhibitedSurfaceFixtures()
    {
        yield return [typeof(RazorParameterFixture), "Value"];
        yield return [typeof(RazorCallbackFixture), "Changed"];
        yield return [typeof(NestedGenericViewModelFixture), "Rows"];
        yield return [typeof(EditDraftFixture), "Baseline"];
        yield return [typeof(CodeBehindStateFixture), "context"];
        yield return [typeof(JsInteropDtoFixture), "Payload"];
    }

    private static IEnumerable<Type> GetProductionV2Types() =>
        typeof(LogicDbContext).Assembly.GetTypes()
            .Where(type => type.Namespace is not null &&
                (type.Namespace == V2Namespace || type.Namespace.StartsWith(V2Namespace + ".", StringComparison.Ordinal)));

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Path.GetDirectoryName(typeof(RoomEditorV2ContractBoundaryTests).Assembly.Location)! })
        {
            for (var directory = new DirectoryInfo(startingPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Silksong Rando Logic Manager.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the V2 source-boundary test.");
    }
}

internal static class RoomEditorV2ContractBoundary
{
    private static readonly Type[] entityTypes = typeof(LogicDbContext).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
        .Select(property => property.PropertyType.GetGenericArguments()[0])
        .ToArray();

    public static IReadOnlyList<string> FindViolations(IEnumerable<Type> contractTypes)
    {
        var violations = new List<string>();
        foreach (var contractType in contractTypes)
        {
            InspectType(contractType, contractType.FullName ?? contractType.Name, new HashSet<Type>(), violations);
        }

        return violations;
    }

    private static void InspectType(Type type, string path, HashSet<Type> visited, List<string> violations)
    {
        type = Unwrap(type);
        if (IsProhibited(type))
        {
            violations.Add($"{path} exposes prohibited {type.FullName}.");
            return;
        }

        foreach (var genericArgument in type.GetGenericArguments())
        {
            InspectType(genericArgument, $"{path}<{genericArgument.Name}>", new HashSet<Type>(visited), violations);
        }

        if (!ShouldInspectMembers(type) || !visited.Add(type))
        {
            return;
        }

        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(members))
        {
            InspectType(field.FieldType, $"{path}.{field.Name}", new HashSet<Type>(visited), violations);
        }

        foreach (var property in type.GetProperties(members))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                InspectType(property.PropertyType, $"{path}.{property.Name}", new HashSet<Type>(visited), violations);
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        return type;
    }

    private static bool IsProhibited(Type type) =>
        typeof(DbContext).IsAssignableFrom(type) ||
        entityTypes.Any(entityType => entityType.IsAssignableFrom(type)) ||
        type == typeof(MapRenderProjectionService) ||
        type == typeof(MapLinkService) ||
        type == typeof(MapManifestService) ||
        type == typeof(MapOverlayService);

    private static bool ShouldInspectMembers(Type type) =>
        type.Namespace is not null && type.Namespace.StartsWith("Silksong_Rando_Logic_Manager.Components.RoomEditorV2", StringComparison.Ordinal) ||
        type.Assembly == typeof(RoomEditorV2ContractBoundary).Assembly;
}

internal sealed class RazorParameterFixture
{
    [Parameter] public Room? Value { get; set; }
}

internal sealed class RazorCallbackFixture
{
    [Parameter] public EventCallback<Room> Changed { get; set; }
}

internal sealed class NestedGenericViewModelFixture
{
    public List<ContractEnvelope<ContractEnvelope<SubroomConnection>>> Rows { get; } = [];
}

internal sealed class EditDraftFixture
{
    public ContractEnvelope<RoomTransition>? Baseline { get; init; }
}

internal sealed class CodeBehindStateFixture
{
    private readonly LogicDbContext? context = null;

    public LogicDbContext? Context => context;
}

internal sealed class JsInteropDtoFixture
{
    public ContractEnvelope<CheckLocation>? Payload { get; init; }
}

internal sealed class ContractEnvelope<T>
{
    public T? Value { get; init; }
}
