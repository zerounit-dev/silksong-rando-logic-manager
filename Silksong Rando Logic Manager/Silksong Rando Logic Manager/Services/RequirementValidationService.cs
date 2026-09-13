using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Silksong_Rando_Logic_Manager.Data;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services;

public enum RequirementBearingRowKind { Transition, Connection, Check }

public sealed record RequirementValidationRowResult(
    RequirementBearingRowKind Kind,
    Guid EntityId,
    Guid RoomId,
    string Requirements,
    bool? Succeeded);

/// <summary>
/// Fresh transient results from one atomic validation preflight. A future graph
/// workflow may consume these results; persisted status alone is not preflight output.
/// </summary>
public sealed record RequirementValidationPreflightResult(
    IReadOnlyList<RequirementValidationRowResult> Rows,
    int Successful,
    int Failed,
    int Unknown);

public interface IRequirementValidationService
{
    Task<RequirementValidationPreflightResult> RecheckAllAsync(CancellationToken cancellationToken = default);
    Task<RequirementValidationPreflightResult> RecheckRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
}

public sealed class RequirementValidationOperationException(string message, Exception innerException) : Exception(message, innerException)
{
}

public sealed class RequirementValidationService(
    IDbContextFactory<LogicDbContext> contexts,
    ILogger<RequirementValidationService> logger) : IRequirementValidationService
{
    public async Task<RequirementValidationPreflightResult> RecheckAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var predicates = await LoadPredicatesAsync(db, cancellationToken);
            var items = await LoadItemsAsync(db, cancellationToken);
            var rooms = await db.Rooms.AsNoTracking().Where(row => !row.IsArchived)
                .Select(row => new RequirementValidationRoom(row.Id, row.ReferenceId)).ToListAsync(cancellationToken);
            var activeRoomIds = rooms.Select(row => row.Id).ToArray();
            var checks = await db.CheckLocations.Where(row => !row.IsArchived && activeRoomIds.Contains(row.RoomId)).ToListAsync(cancellationToken);
            var transitions = await db.RoomTransitions.Where(row => !row.IsArchived && activeRoomIds.Contains(row.RoomId)).ToListAsync(cancellationToken);
            var connections = await db.SubroomConnections.Where(row => !row.IsArchived && activeRoomIds.Contains(row.RoomId)).ToListAsync(cancellationToken);
            var context = new RequirementValidationContext(predicates, items, rooms,
                checks.Select(row => new RequirementValidationCheck(row.Id, row.RoomId, row.FriendlyName)).ToArray());

            var results = new List<RequirementValidationRowResult>(transitions.Count + connections.Count + checks.Count);
            foreach (var row in transitions)
                Apply(row, RequirementBearingRowKind.Transition, ValidateSafely(context, row.RoomId, row.Id, RequirementBearingRowKind.Transition, row.Requirements), results);
            foreach (var row in connections)
                Apply(row, RequirementBearingRowKind.Connection, ValidateSafely(context, row.RoomId, row.Id, RequirementBearingRowKind.Connection, row.Requirements), results);
            foreach (var row in checks)
                Apply(row, RequirementBearingRowKind.Check, ValidateSafely(context, row.RoomId, row.Id, RequirementBearingRowKind.Check, row.Requirements), results);

            using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreateResult(results);
        }
        catch (Exception exception) when (ClassifySqlite(exception) is not null)
        {
            logger.LogWarning(exception, "Atomic global requirement validation was rejected by SQLite; no statuses were committed.");
            throw new RequirementValidationOperationException(ClassifySqlite(exception)!, exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Atomic global requirement validation failed; no statuses were committed.");
            throw;
        }

    }

    private static void Apply(ArchivableEntity row, RequirementBearingRowKind kind, bool? status, List<RequirementValidationRowResult> results)
    {
        var roomId = row switch
        {
            RoomTransition value => value.RoomId,
            SubroomConnection value => value.RoomId,
            CheckLocation value => value.RoomId,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        var requirements = row switch
        {
            RoomTransition value => value.Requirements,
            SubroomConnection value => value.Requirements,
            CheckLocation value => value.Requirements,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        switch (row)
        {
            case RoomTransition value when value.RequirementsParseSucceeded != status: value.RequirementsParseSucceeded = status; break;
            case SubroomConnection value when value.RequirementsParseSucceeded != status: value.RequirementsParseSucceeded = status; break;
            case CheckLocation value when value.RequirementsParseSucceeded != status: value.RequirementsParseSucceeded = status; break;
        }
        results.Add(new(kind, row.Id, roomId, requirements, status));
    }

    public async Task<RequirementValidationPreflightResult> RecheckRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var activeRoom = await db.Rooms.AsNoTracking().Where(row => row.Id == roomId && !row.IsArchived)
                .Select(row => new RequirementValidationRoom(row.Id, row.ReferenceId)).SingleOrDefaultAsync(cancellationToken);
            if (activeRoom is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return CreateResult([]);
            }

            var context = await LoadContextAsync(db, cancellationToken);
            var checks = await db.CheckLocations.Where(row => row.RoomId == roomId && !row.IsArchived && !row.Room.IsArchived).ToListAsync(cancellationToken);
            var transitions = await db.RoomTransitions.Where(row => row.RoomId == roomId && !row.IsArchived && !row.Room.IsArchived).ToListAsync(cancellationToken);
            var connections = await db.SubroomConnections.Where(row => row.RoomId == roomId && !row.IsArchived && !row.Room.IsArchived).ToListAsync(cancellationToken);

            var results = new List<RequirementValidationRowResult>(transitions.Count + connections.Count + checks.Count);
            foreach (var row in transitions)
                Apply(row, RequirementBearingRowKind.Transition, ValidateSafely(context, row.RoomId, row.Id, RequirementBearingRowKind.Transition, row.Requirements), results);
            foreach (var row in connections)
                Apply(row, RequirementBearingRowKind.Connection, ValidateSafely(context, row.RoomId, row.Id, RequirementBearingRowKind.Connection, row.Requirements), results);
            foreach (var row in checks)
                Apply(row, RequirementBearingRowKind.Check, ValidateSafely(context, row.RoomId, row.Id, RequirementBearingRowKind.Check, row.Requirements), results);

            using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreateResult(results);
        }
        catch (Exception exception) when (ClassifySqlite(exception) is not null)
        {
            logger.LogWarning(exception, "Atomic current-room requirement validation was rejected by SQLite; no statuses were committed.");
            throw new RequirementValidationOperationException(ClassifySqlite(exception)!, exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Atomic current-room requirement validation failed; no statuses were committed.");
            throw;
        }
    }

    internal async Task<RequirementValidationContext> LoadContextAsync(LogicDbContext db, CancellationToken cancellationToken)
    {
        var predicates = await LoadPredicatesAsync(db, cancellationToken);
        var items = await LoadItemsAsync(db, cancellationToken);
        var rooms = await db.Rooms.AsNoTracking().Where(row => !row.IsArchived)
            .Select(row => new RequirementValidationRoom(row.Id, row.ReferenceId)).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.AsNoTracking().Where(row => !row.IsArchived && !row.Room.IsArchived)
            .Select(row => new RequirementValidationCheck(row.Id, row.RoomId, row.FriendlyName)).ToListAsync(cancellationToken);
        return new(predicates, items, rooms, checks);
    }

    internal bool? ValidateSafely(RequirementValidationContext context, Guid roomId, Guid rowId,
        RequirementBearingRowKind kind, string requirements)
    {
        if (string.IsNullOrWhiteSpace(requirements)) return null;
        try
        {
            return ManagedRequirementValidator.Validate(requirements, roomId, context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Unexpected managed requirement validator fault for {Kind} {RowId} in room {RoomId}; status is unknown.",
                kind, rowId, roomId);
            return null;
        }
    }

    private static Task<List<RequirementValidationPredicate>> LoadPredicatesAsync(LogicDbContext db, CancellationToken token) =>
        db.RequirementPredicates.AsNoTracking().OrderBy(row => row.SortOrder).ThenBy(row => row.Id)
            .Select(row => new RequirementValidationPredicate(row.Id, row.InputSyntax, row.Aliases)).ToListAsync(token);

    private static Task<List<RequirementValidationItem>> LoadItemsAsync(LogicDbContext db, CancellationToken token) =>
        db.RequirementItems.AsNoTracking().OrderBy(row => row.SortOrder).ThenBy(row => row.Id)
            .Select(row => new RequirementValidationItem(row.Id, row.Aliases)).ToListAsync(token);

    private static RequirementValidationPreflightResult CreateResult(IReadOnlyList<RequirementValidationRowResult> rows) =>
        new(rows, rows.Count(row => row.Succeeded == true), rows.Count(row => row.Succeeded == false), rows.Count(row => row.Succeeded is null));

    private static string? ClassifySqlite(Exception exception)
    {
        var sqlite = exception as SqliteException ?? exception.InnerException as SqliteException;
        return sqlite?.SqliteErrorCode switch
        {
            5 or 6 => "The requirements database is busy or locked. Close another writer and try the recheck again.",
            8 => "The requirements database is read-only. Restore write access and try the recheck again.",
            10 or 14 => "The requirements database could not be written. Check that its folder is available and try again.",
            19 => "The requirements database rejected the status update. Reload the application and try again.",
            _ => null
        };
    }
}

internal sealed record RequirementValidationPredicate(Guid Id, string InputSyntax, string Aliases);
internal sealed record RequirementValidationItem(Guid Id, string Aliases);
internal sealed record RequirementValidationRoom(Guid Id, string ReferenceId);
internal sealed record RequirementValidationCheck(Guid Id, Guid RoomId, string FriendlyName);
internal sealed record RequirementValidationContext(
    IReadOnlyList<RequirementValidationPredicate> Predicates,
    IReadOnlyList<RequirementValidationItem> Items,
    IReadOnlyList<RequirementValidationRoom> Rooms,
    IReadOnlyList<RequirementValidationCheck> Checks);

internal static class ManagedRequirementValidator
{
    private static readonly HashSet<string> Difficulties = new(StringComparer.Ordinal) { "easy", "medium", "hard" };
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "left", "right", "up", "down" };

    internal static bool Validate(string source, Guid owningRoomId, RequirementValidationContext context) =>
        new Parser(source, owningRoomId, Prepare(context)).Parse();

    private static PreparedContext Prepare(RequirementValidationContext context)
    {
        var predicates = context.Predicates.SelectMany(predicate =>
        {
            if (!RequirementCatalogueLanguage.TryParsePersistedValue(predicate.InputSyntax, out var syntax))
                throw new InvalidOperationException($"Requirement predicate {predicate.Id} has unsupported input syntax.");
            if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(predicate.Aliases, out var canonical, out var aliases, out _) ||
                !string.Equals(canonical, predicate.Aliases, StringComparison.Ordinal))
                throw new InvalidOperationException($"Requirement predicate {predicate.Id} has invalid or noncanonical aliases.");
            return aliases.Select(alias => new PreparedPredicate(syntax, alias, RequirementCatalogueLanguage.NormalizeIdentification(alias)));
        }).ToArray();
        var items = context.Items.SelectMany(item =>
        {
            if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(item.Aliases, out var canonical, out var aliases, out _) ||
                !string.Equals(canonical, item.Aliases, StringComparison.Ordinal))
                throw new InvalidOperationException($"Requirement item {item.Id} has invalid or noncanonical aliases.");
            return aliases.Select(RequirementCatalogueLanguage.NormalizeIdentification);
        }).ToArray();
        EnsureUnique(predicates.Select(value => value.NormalizedAlias), "predicate");
        EnsureUnique(items, "item");
        return new(predicates, items, context.Rooms, context.Checks);
    }

    private static void EnsureUnique(IEnumerable<string> aliases, string kind)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (aliases.Any(alias => !found.Add(alias)))
            throw new InvalidOperationException($"The requirement {kind} catalogue contains a normalized alias collision.");
    }

    private sealed record PreparedPredicate(RequirementInputSyntax Syntax, string Alias, string NormalizedAlias);
    private sealed record PreparedContext(IReadOnlyList<PreparedPredicate> Predicates, IReadOnlyList<string> Items,
        IReadOnlyList<RequirementValidationRoom> Rooms, IReadOnlyList<RequirementValidationCheck> Checks);

    private sealed class Parser(string source, Guid owningRoomId, PreparedContext context)
    {
        private int position;

        internal bool Parse()
        {
            SkipWhitespace();
            return position < source.Length && ParseExpression(false) && position == source.Length;
        }

        private bool ParseExpression(bool grouped)
        {
            string? operatorKind = null;
            var hasOperand = false;
            while (true)
            {
                SkipWhitespace();
                if (position >= source.Length) return !grouped && hasOperand;
                if (source[position] == ')')
                {
                    if (!grouped || !hasOperand) return false;
                    position++;
                    return true;
                }
                if (hasOperand)
                {
                    var next = WordAt("AND") ? "AND" : WordAt("OR") ? "OR" : null;
                    if (next is null || operatorKind is not null && operatorKind != next) return false;
                    operatorKind = next;
                    position += next.Length;
                    SkipWhitespace();
                    if (position >= source.Length || source[position] == ')') return false;
                }

                var atomic = source[position] != '(';
                if (atomic)
                {
                    var start = position;
                    while (position < source.Length && source[position] is not '(' and not ')' && !WordAt("AND") && !WordAt("OR")) position++;
                    if (!ValidateAtom(source[start..position])) return false;
                }
                else
                {
                    position++;
                    if (!ParseExpression(true)) return false;
                }
                hasOperand = true;
                SkipWhitespace();
                if (atomic && position < source.Length && source[position] == '(' && !ParseAnnotation()) return false;
            }
        }

        private bool ParseAnnotation()
        {
            position++;
            while (position < source.Length && source[position] != ')')
            {
                if (source[position] is '(' or '\\') return false;
                position++;
            }
            if (position >= source.Length) return false;
            position++;
            return true;
        }

        private bool ValidateAtom(string raw)
        {
            var text = raw.Trim();
            if (text.Length == 0) return false;
            var normalized = RequirementCatalogueLanguage.NormalizeIdentification(text);
            var candidates = new List<bool>();
            foreach (var predicate in context.Predicates)
            {
                switch (predicate.Syntax)
                {
                    case RequirementInputSyntax.PredicateOnly:
                        if (normalized == predicate.NormalizedAlias) candidates.Add(true);
                        break;
                    case RequirementInputSyntax.OptionalDifficultyThenPredicate:
                        if (normalized == predicate.NormalizedAlias || HasLeadingValue(normalized, predicate.NormalizedAlias, Difficulties)) candidates.Add(true);
                        break;
                    case RequirementInputSyntax.RequiredDifficultyThenPredicate:
                        if (HasLeadingValue(normalized, predicate.NormalizedAlias, Difficulties)) candidates.Add(true);
                        break;
                    case RequirementInputSyntax.PredicateThenDirection:
                        if (TryRemainder(normalized, predicate.NormalizedAlias, out var direction) && Directions.Contains(direction)) candidates.Add(true);
                        break;
                    case RequirementInputSyntax.PredicateThenItem:
                        if (TryRemainder(normalized, predicate.NormalizedAlias, out var item))
                            candidates.AddRange(context.Items.Where(alias => alias == item).Select(_ => true));
                        break;
                    case RequirementInputSyntax.PredicateThenQuantity:
                        if (TryRemainder(normalized, predicate.NormalizedAlias, out var quantity) && quantity.Length > 0 && quantity[0] is >= '1' and <= '9' &&
                            quantity.All(character => character is >= '0' and <= '9'))
                            candidates.Add(true);
                        break;
                    case RequirementInputSyntax.PredicateThenCheck:
                        var prefix = PrefixLength(text, predicate.Alias);
                        if (prefix >= 0 && TryDependency(text[prefix..], owningRoomId, out var resolved)) candidates.Add(resolved);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            return candidates.Count == 1 && candidates[0];
        }

        private bool TryDependency(string remainder, Guid roomId, out bool resolved)
        {
            resolved = false;
            var operand = remainder.TrimStart();
            if (operand.Length == 0) return false;
            var qualifiers = FindQualifiers(operand);
            if (qualifiers.Any(value => value.Word == "GLOBAL")) return false;
            Guid? targetRoom = roomId;
            string checkName;
            if (qualifiers.Count == 0) checkName = operand;
            else if (qualifiers.Count == 1 && qualifiers[0].Word == "THE" && qualifiers[0].Index == 0)
            {
                targetRoom = null;
                checkName = operand[3..].TrimStart();
            }
            else if (qualifiers.Count == 1 && qualifiers[0].Word == "IN" && qualifiers[0].Index > 0)
            {
                var qualifier = qualifiers[0];
                checkName = operand[..qualifier.Index].TrimEnd();
                var roomText = operand[(qualifier.Index + 2)..].TrimStart();
                if (checkName.Length == 0 || roomText.Length == 0) return false;
                var roomMatches = context.Rooms.Where(room => RequirementCatalogueLanguage.NormalizeIdentification(room.ReferenceId) == RequirementCatalogueLanguage.NormalizeIdentification(roomText)).ToArray();
                if (roomMatches.Length != 1) return true;
                targetRoom = roomMatches[0].Id;
            }
            else return false;
            if (checkName.Trim().Length == 0) return false;
            var identity = RequirementCatalogueLanguage.NormalizeIdentification(checkName);
            resolved = context.Checks.Count(check => (targetRoom is null || check.RoomId == targetRoom) &&
                RequirementCatalogueLanguage.NormalizeIdentification(check.FriendlyName) == identity) == 1;
            return true;
        }

        private static List<(string Word, int Index)> FindQualifiers(string text)
        {
            var result = new List<(string, int)>();
            for (var index = 0; index < text.Length; index++)
                foreach (var word in new[] { "IN", "THE", "GLOBAL" })
                    if (WordAt(text, word, index)) { result.Add((word, index)); index += word.Length - 1; break; }
            return result;
        }

        private bool WordAt(string word) => WordAt(source, word, position);
        private static bool WordAt(string text, string word, int index) => index + word.Length <= text.Length &&
            text.AsSpan(index, word.Length).SequenceEqual(word) &&
            !IsIdentificationRuneBefore(text, index) &&
            !IsIdentificationRuneAt(text, index + word.Length);

        private static bool IsIdentificationRuneBefore(string text, int index) => index > 0 &&
            Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var rune, out _) == OperationStatus.Done &&
            IsIdentificationRune(rune);

        private static bool IsIdentificationRuneAt(string text, int index) => index < text.Length &&
            Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _) == OperationStatus.Done &&
            IsIdentificationRune(rune);

        private static bool IsIdentificationRune(Rune rune) =>
            Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber;

        private static int PrefixLength(string text, string alias)
        {
            var target = RequirementCatalogueLanguage.NormalizeIdentification(alias);
            for (var end = 1; end <= text.Length; end++)
                if (RequirementCatalogueLanguage.NormalizeIdentification(text[..end]) == target &&
                    (end == text.Length || char.IsWhiteSpace(text[end]))) return end;
            return -1;
        }

        private static bool HasLeadingValue(string value, string alias, IReadOnlySet<string> values)
        {
            var split = value.IndexOf(' ');
            return split > 0 && values.Contains(value[..split]) && value[(split + 1)..] == alias;
        }

        private static bool TryRemainder(string value, string alias, out string remainder)
        {
            var prefix = alias + " ";
            if (value.StartsWith(prefix, StringComparison.Ordinal)) { remainder = value[prefix.Length..]; return true; }
            remainder = string.Empty;
            return false;
        }

        private void SkipWhitespace()
        {
            while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
        }
    }
}
