namespace Silksong_Rando_Logic_Manager.Authentication;

internal sealed record CommandIdentity(string Owner, Services.HistoryActor Actor);

internal interface ICommandAdmission
{
    Task<CommandIdentity> AuthorAsync(CancellationToken token);
}

internal sealed class WebCommandAdmission(LiveAccess access) : ICommandAdmission
{
    public async Task<CommandIdentity> AuthorAsync(CancellationToken token)
    {
        var identity = await access.RequireAsync(AccessPolicies.Author, token);
        return new(identity.Owner, Services.HistoryActor.From(identity));
    }
}

// A20: constructed only by the explicitly named standalone composition factories.
// Never registered in web DI and never selected from request/ambient state.
internal sealed class StandaloneBulkImportAdmission : ICommandAdmission
{
    public Task<CommandIdentity> AuthorAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new CommandIdentity("standalone-bulk-import", Services.HistoryActor.StandaloneOperator));
    }
}
