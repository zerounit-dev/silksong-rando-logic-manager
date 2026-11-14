using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomAudit;

// The public component parameter exposes only the purpose-shaped published view.
// Structural handles stay assembly-local under the existing analysis/context owner.
public static class AuditSemanticProjection
{
    internal sealed record Section(string? Origin, AuditFactContext Facts, ImmutableArray<AuditPreparedEntry> Entries);
    internal sealed record Group(ImmutableArray<AuditCondition> Header, AuditFactContext HeaderFacts, ImmutableArray<Section> Sections);

    // Cheap current-context binding only. No structural operation or retained facts.
    internal static IEnumerable<Group> Bind(PreparedAuditRequirementView view,
        AuditFactContext context, IReadOnlyList<AuditOriginComparison> comparisons)
    {
        foreach (var group in view.Groups)
        {
            var sections = ImmutableArray.CreateBuilder<Section>();
            if (comparisons.Count == 0 || !group.HasLocal)
                sections.Add(new(null, comparisons.Count == 1 ? comparisons[0].Evaluation.Facts : context, group.Entries));
            else foreach (var comparison in comparisons)
            {
                var entries = group.Entries;
                if (entries.Length > 0) sections.Add(new(comparisons.Count > 1 ? comparison.Label : null, comparison.Evaluation.Facts, entries));
            }
            yield return new(group.Header, context, sections.ToImmutable());
        }
    }
}
