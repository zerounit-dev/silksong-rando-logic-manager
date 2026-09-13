namespace Silksong_Rando_Logic_Manager.Services;

public sealed class TransitionInverseSetupState
{
    private readonly Queue<Guid> sourceTransitionIds = new();

    public void Enqueue(IEnumerable<Guid> transitionIds)
    {
        foreach (var transitionId in transitionIds) sourceTransitionIds.Enqueue(transitionId);
    }

    public bool TryDequeue(out Guid transitionId) => sourceTransitionIds.TryDequeue(out transitionId);
}
