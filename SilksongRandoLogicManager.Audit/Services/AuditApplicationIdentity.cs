using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal readonly record struct AuditApplicationHandle(int Index);
internal readonly record struct AuditOccurrenceHandle(int Index);
internal readonly record struct AuditExpressionAddress(int Index);
internal readonly record struct AuditOperandIdentity(string Role, Guid? Id, string Value);

// Not a serialized key. Operand arrays require structural ordered equality, while
// source spelling/span, row, catalogue names and current facts are deliberately absent.
internal sealed class AuditApplicationKey : IEquatable<AuditApplicationKey>
{
    internal Guid PredicateId { get; }
    internal string Signature { get; }
    internal RequirementAtomFormatArguments Arguments { get; }
    internal ImmutableArray<AuditOperandIdentity> Operands { get; }
    internal Guid? DependencyId { get; }
    private readonly int hash;

    internal AuditApplicationKey(AuditAtom atom)
    {
        PredicateId = atom.PredicateId; Signature = atom.Signature;
        Arguments = atom.Arguments; DependencyId = atom.DependencyId;
        Operands = atom.Operands.Select(x => new AuditOperandIdentity(x.Role, x.Id, x.Value)).ToImmutableArray();
        var code = new HashCode(); code.Add(PredicateId); code.Add(Signature, StringComparer.Ordinal);
        code.Add(Arguments); code.Add(DependencyId);
        foreach (var operand in Operands) code.Add(operand);
        hash = code.ToHashCode();
    }

    public bool Equals(AuditApplicationKey? other) => other is not null && hash == other.hash &&
        PredicateId == other.PredicateId && StringComparer.Ordinal.Equals(Signature, other.Signature) &&
        Arguments == other.Arguments && DependencyId == other.DependencyId && Operands.SequenceEqual(other.Operands);
    public override bool Equals(object? other) => other is AuditApplicationKey key && Equals(key);
    public override int GetHashCode() => hash;
}

internal sealed record PreparedAuditApplication(AuditApplicationHandle Handle, AuditApplicationKey Key,
    AuditPredicateDefinitionPlan Definition)
{
    // B9's finite typed difficulty-bearing shapes, independent of binding/signature.
    internal bool RequiresAdvanced { get; } = (Definition.Definition.InputShape is
        RequirementInputSyntax.OptionalDifficultyThenPredicate or RequirementInputSyntax.RequiredDifficultyThenPredicate or
        RequirementInputSyntax.RequiredDifficultyThenPredicateThenSoul or RequirementInputSyntax.RequiredDifficultyThenCrestThenPredicate or
        RequirementInputSyntax.RequiredDifficultyThenOptionalCrestThenPredicate or RequirementInputSyntax.RequiredDifficultyThenMethodThenPredicate or
        RequirementInputSyntax.RequiredDifficultyThenPredicateThenOptionalQuantity or RequirementInputSyntax.RequiredDifficultyThenPredicateThenQuantity or
        RequirementInputSyntax.RequiredDifficultyThenMethodThenPredicateThenOptionalQuantity or
        RequirementInputSyntax.RequiredDifficultyThenPredicateThenDirection or
        RequirementInputSyntax.RequiredDifficultyThenCrestThenPredicateThenOptionalQuantity)
        && Key.Arguments.Difficulty is "easy" or "medium" or "hard" or "cbt";
    internal bool Admitted(AuditRequirementsContext context) => context.Advanced || !RequiresAdvanced;
}
// A parent link + child slot records the actual row/tree address in linear space,
// instead of copying a progressively longer path string at every descendant.
internal sealed record PreparedAuditAddress(AuditRowHandle Row, AuditExpressionAddress? Parent, int Child);
internal sealed record PreparedAuditOccurrence(AuditOccurrenceHandle Handle, AuditRowHandle Row,
    AuditExpressionAddress Address, AuditApplicationHandle Application, AuditAtom Source);
internal abstract record PreparedAuditExpression;
internal sealed record PreparedAuditBoolean(bool IsAnd, PreparedAuditExpression Left,
    PreparedAuditExpression Right) : PreparedAuditExpression;
internal sealed record PreparedAuditApplicationUse(AuditOccurrenceHandle Occurrence) : PreparedAuditExpression;
internal sealed record PreparedAuditUnresolved(ImmutableArray<string> Reasons) : PreparedAuditExpression;

internal sealed class AuditSourceInventory
{
    internal ImmutableArray<PreparedAuditApplication> Applications { get; }
    internal ImmutableArray<PreparedAuditOccurrence> Occurrences { get; }
    internal ImmutableArray<PreparedAuditAddress> Addresses { get; }
    internal ImmutableArray<PreparedAuditExpression> RowRoots { get; }
    internal ImmutableArray<ImmutableArray<AuditOccurrenceHandle>> RowOccurrences { get; }

    internal AuditSourceInventory(PreparedAuditRoom room, CancellationToken token)
    {
        var applications = ImmutableArray.CreateBuilder<PreparedAuditApplication>();
        var occurrences = ImmutableArray.CreateBuilder<PreparedAuditOccurrence>();
        var addresses = ImmutableArray.CreateBuilder<PreparedAuditAddress>();
        var roots = ImmutableArray.CreateBuilder<PreparedAuditExpression>(room.Rows.Length);
        var rowOccurrences = ImmutableArray.CreateBuilder<ImmutableArray<AuditOccurrenceHandle>>(room.Rows.Length);
        var byKey = new Dictionary<AuditApplicationKey, AuditApplicationHandle>();
        foreach (var row in room.Rows)
        {
            token.ThrowIfCancellationRequested();
            var rowUses = ImmutableArray.CreateBuilder<AuditOccurrenceHandle>();
            roots.Add(row.Source.Requirement.Expression is { } expression ? Visit(expression, null, -1) :
                new PreparedAuditUnresolved(row.Source.Requirement.Reasons.ToImmutableArray()));
            rowOccurrences.Add(rowUses.ToImmutable());
            PreparedAuditExpression Visit(AuditExpression expression, AuditExpressionAddress? parent, int child)
            {
                token.ThrowIfCancellationRequested();
                var address = new AuditExpressionAddress(addresses.Count);
                addresses.Add(new(row.Handle, parent, child));
                if (expression is AuditBoolean boolean)
                    return new PreparedAuditBoolean(boolean.IsAnd, Visit(boolean.Left, address, 0), Visit(boolean.Right, address, 1));
                var atom = (AuditAtom)expression;
                var key = new AuditApplicationKey(atom);
                if (!byKey.TryGetValue(key, out var application))
                {
                    application = new(applications.Count);
                    byKey.Add(key, application);
                    applications.Add(new(application, key, room.DefinitionByGuid[atom.PredicateId]));
                }
                var occurrence = new AuditOccurrenceHandle(occurrences.Count);
                occurrences.Add(new(occurrence, row.Handle, address, application, atom));
                rowUses.Add(occurrence);
                return new PreparedAuditApplicationUse(occurrence);
            }
        }
        token.ThrowIfCancellationRequested();
        Applications = applications.ToImmutable(); Occurrences = occurrences.ToImmutable();
        Addresses = addresses.ToImmutable(); RowRoots = roots.ToImmutable(); RowOccurrences = rowOccurrences.ToImmutable();
    }
}
