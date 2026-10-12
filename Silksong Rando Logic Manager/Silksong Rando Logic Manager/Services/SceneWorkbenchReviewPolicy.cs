namespace Silksong_Rando_Logic_Manager.Services;

// Modal review defaults only. Shared classification and operational import never call this policy.
public static class SceneWorkbenchReviewPolicy
{
    public static void ApplyAdmission(SceneDumpReview review)
    {
        foreach (var node in Flatten(review.RootObjects))
        {
            if (node.Classification != SceneDumpClassification.Other) continue;
            if (node.Components.Any(component => component.IsArchipelagoLocationPickup))
            {
                node.Classification = SceneDumpClassification.Check;
                node.CandidateReasons.Add("Modal Check review default: verified direct CollectableItemPickup AP location-item type (existing persistent ID or own object name retained)");
                continue;
            }
            if (node.Components.Any(component => component.Type == "Remasker"))
            {
                node.CandidateReasons.Add("Modal Check review default excluded: directly owned Remasker without verified AP location pickup; manual Check remains available");
                continue;
            }
            var signals = node.Components.Where(component =>
                component.Type is "BellBench" or "RestBench" or "Lever_tk2d" or "Lever" or
                    "PersistentPressurePlate" or "Gate" or "RosaryCacheString" or "GeoControl" or "BreakableHolder" ||
                component.Type.StartsWith("Persistent", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(component.ItemDataId))
                .Select(component => component.Type).Distinct(StringComparer.Ordinal).ToArray();
            // Retained keys belong to components on this object, never to relatives/references.
            var persistentBreakable = node.Components.Any(component => component.Type == "Breakable") &&
                node.Components.Any(component => !string.IsNullOrWhiteSpace(component.ItemDataId));
            if (signals.Length == 0 && !persistentBreakable) continue;
            node.Classification = SceneDumpClassification.Check;
            foreach (var signal in signals) node.CandidateReasons.Add($"Modal Check review default: {signal}");
            if (persistentBreakable) node.CandidateReasons.Add("Modal Check review default: Breakable with directly owned retained persistent key");
        }
    }

    public static SceneWorkbenchEnemyEvidence CompareEnemyEvidence(SceneDumpObject node, string requirements, string notes)
    {
        var incomingRequirement = SceneEnemySpawnNotesResolver.EnemyRequirements(node);
        var incomingNotes = SceneEnemySpawnNotesResolver.EnemyNotes(node);
        var requirementRecorded = EqualField(requirements, incomingRequirement) ||
            (incomingRequirement is "Normal World Spawn" or "Black Thread World Spawn") &&
                requirements.Split('\n').Any(line => line.Trim() == incomingRequirement);
        var facts = node.SpawnConditions.Select(test => $"{test.Field}={(test.Value ? "true" : "false")}").ToArray();
        var notesRecorded = EqualField(notes, incomingNotes);
        IReadOnlyList<SceneWorkbenchEvidencePiece> pieces;
        if (node.InactiveEnemyReason.Length > 0)
            pieces = [Piece(incomingNotes, notesRecorded, false)];
        else
        {
            // Enumerate captured literal spellings, including opposite values to shield delimiters
            // inside criterion names. This is documentation comparison, not requirement parsing.
            var spellings = node.SpawnConditions.SelectMany(test => new[] { $"{test.Field}=true", $"{test.Field}=false" })
                .Distinct(StringComparer.Ordinal).ToArray();
            var recorded = LiteralClauses(requirements, spellings, false).Concat(LiteralClauses(notes, spellings, true))
                .ToHashSet(StringComparer.Ordinal);
            pieces = facts.Select(fact => Piece(fact, notesRecorded || recorded.Contains(fact), false)).ToArray();
        }
        var status = new List<string>();
        if (node.HasGoverningConditions) status.Add("Conditions found.");
        if (node.FsmBooleanNames.Count > 0) status.Add("FSM Boolean reads found; names alone do not prove governing prerequisites.");
        if (node.HasPersistenceContext) status.Add("Persistence flag context found; captured values do not prove placement invalidity or governing prerequisites.");
        if (node.HasIncompleteConditionEvidence) status.Add("Some conditions could not be read.");
        if (node.HasIncompletePersistenceEvidence) status.Add("Some persistence flag contents could not be read.");
        if (!node.HasGoverningConditions && !node.HasIncompleteConditionEvidence && node.FsmBooleanNames.Count == 0 &&
            !node.HasPersistenceContext && !node.HasIncompletePersistenceEvidence) status.Add("No conditions found.");
        status.Add("Empty or unprinted evidence does not mean there are no conditions.");
        var fsmSection = SceneEnemySpawnNotesResolver.FsmNotes(node);
        var fsmRecorded = notesRecorded || fsmSection.Length > 0 && notes.Split('\n').Any(line => line.Trim() == fsmSection.Trim());
        var fsmPiece = new SceneWorkbenchEvidencePiece(fsmSection, fsmRecorded,
            fsmRecorded ? "Complete FSM Boolean flags section already recorded in your Notes (exact literal comparison)"
                : "Complete FSM Boolean flags section not found in your Notes (exact literal comparison, not proof of governing prerequisites)");
        return new(Piece(incomingRequirement, requirementRecorded, true), pieces,
            node.InactiveEnemyReason.Length == 0 && facts.Length > 0, string.Join(' ', status),
            node.HasIncompleteConditionEvidence || node.HasIncompletePersistenceEvidence, fsmPiece);
    }

    private static bool EqualField(string authored, string incoming) => incoming.Length > 0 && authored.Trim() == incoming.Trim();
    private static SceneWorkbenchEvidencePiece Piece(string text, bool recorded, bool requirement) => new(text, recorded,
        recorded ? "Already recorded" : requirement ? "Not found in your Requirements (literal comparison, not logical absence)"
            : "Not found in your Requirements or Notes (literal comparison, not logical absence)");

    private static IEnumerable<string> LiteralClauses(string authored, IReadOnlyList<string> spellings, bool allowPrefix)
    {
        var matches = new List<(int Start, int End, string Text)>();
        for (var boundary = 0; boundary <= authored.Length; boundary++)
        {
            if (boundary > 0 && !Delimiter(authored[boundary - 1])) continue;
            var start = boundary;
            while (start < authored.Length && char.IsWhiteSpace(authored[start])) start++;
            const string prefix = "Spawn conditions:";
            if (allowPrefix && authored.AsSpan(start).StartsWith(prefix, StringComparison.Ordinal))
            {
                start += prefix.Length;
                while (start < authored.Length && char.IsWhiteSpace(authored[start])) start++;
            }
            foreach (var spelling in spellings)
            {
                if (!authored.AsSpan(start).StartsWith(spelling, StringComparison.Ordinal)) continue;
                var end = start + spelling.Length;
                var tail = end;
                while (tail < authored.Length && char.IsWhiteSpace(authored[tail]) && !Delimiter(authored[tail])) tail++;
                if (tail == authored.Length || Delimiter(authored[tail])) matches.Add((start, end, spelling));
            }
        }
        // A comma/newline/semicolon embedded in a known literal name is not a new fact.
        return matches.Where(match => !matches.Any(outer => outer.Start <= match.Start && outer.End >= match.End &&
            (outer.Start < match.Start || outer.End > match.End)))
            .Select(match => match.Text);
    }

    private static bool Delimiter(char value) => value is '\n' or '\r' or ';' or ',';
    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
}

public sealed record SceneWorkbenchEvidencePiece(string Text, bool AlreadyRecorded, string Explanation);
public sealed record SceneWorkbenchEnemyEvidence(SceneWorkbenchEvidencePiece Requirement, IReadOnlyList<SceneWorkbenchEvidencePiece> Notes,
    bool HasFiniteFacts, string StatusExplanation, bool Incomplete, SceneWorkbenchEvidencePiece FsmSection);
