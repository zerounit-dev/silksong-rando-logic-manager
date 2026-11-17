namespace Silksong_Rando_Logic_Manager.Services;

public sealed record ScenePreviewRequest(string SceneName, IReadOnlyList<string> IdentityKeys, IReadOnlyList<string> DestinationKeys);
public sealed record SceneForeignMatch(string IdentityKey, Guid RecordId, Guid RoomId, SceneDumpClassification Family);
public sealed record SceneReviewEvidence(SceneImportPreview Preview, IReadOnlyList<SceneForeignMatch> ForeignMatches, string Fingerprint, SceneDestinationObservation Destinations);
public sealed record SceneAppliedReceipt(Guid RoomId, IReadOnlyList<Guid> CreatedTransitionIds, IReadOnlyList<Guid> CreatedCheckIds, IReadOnlyList<Guid> AppliedExitIds);
public sealed record SceneApplyResult(SceneAppliedReceipt Receipt, SilksongRandoLogicManager.Contracts.SharedMutationSegment? Patch,
    SilksongRandoLogicManager.Contracts.SceneAffectedRecovery Recovery)
{
    public string? HistoryWarning { get; init; }
}
public sealed record SceneInverseProposal(Guid SourceId, Guid TargetId, string SourceName, string TargetName, string RoomReference, string Alias, bool FillRoom, bool FillAlias, string Baseline);
public sealed record SceneInversePrepare(Guid SourceId);
public sealed record SceneInverseConfirm(Guid SourceId, string Baseline);
public sealed record SceneInverseResult(bool Applied, string? Detail, SilksongRandoLogicManager.Contracts.SharedMutationSegment? Patch,
    SilksongRandoLogicManager.Contracts.SceneAffectedRecovery Recovery)
{
    public string? HistoryWarning { get; init; }
}
public sealed class SceneTransportException : Exception;

public static class SceneTransport
{
    public static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> roots)
    {
        var stack = new Stack<IEnumerator<SceneDumpObject>>();
        stack.Push(roots.GetEnumerator());
        try
        {
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop().Dispose(); continue; }
                var node = stack.Peek().Current;
                yield return node;
                stack.Push(node.Children.GetEnumerator());
            }
        }
        finally { while (stack.Count > 0) stack.Pop().Dispose(); }
    }

}
