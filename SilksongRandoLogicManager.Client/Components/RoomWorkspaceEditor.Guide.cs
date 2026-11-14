using System.Collections.Immutable;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private RequirementCatalogueReferenceView? guide;
    private bool guideLoading, guideFocusPending;
    private RequirementsParserContextView? appliedParserContext;
    private IJSInProcessObjectReference? appliedParserOwner;
    private const string ReminderFooterProse = "remember your options: ledge grab, spike pogo, crest pogo, run, dash, drifter's cloak, faydown cloak, cling grip, silk soar, clawline, sharpdart, scuttlebrace";

    private RequirementsParserContextView ParserContext()
    {
        var vocabulary = Catalogue.Vocabulary ?? throw new InvalidOperationException("The confirmed requirement vocabulary is not ready.");
        var rooms = Catalogue.PresentationRoomIds.Select(id => Catalogue.Rooms[id]).Where(x => !x.Header.Observation.IsArchived).ToArray();
        return new(rooms.Select(x => new RequirementsParserRoomView(x.Observation.RoomId, x.Header.Values.ReferenceId)).ToArray(),
            rooms.SelectMany(room => room.Checks.Where(x => !x.Observation.IsArchived).Select(x => new RequirementsParserCheckView(room.Observation.RoomId, x.Values.FriendlyName))).ToArray(),
            vocabulary.Kinds[0].Select(x => new RequirementsParserPredicateView(x.Value.InputSyntax!, x.ParsedAliases)).ToArray(),
            vocabulary.Kinds[3].Select(x => new RequirementsParserItemView(x.ParsedAliases)).ToArray(),
            vocabulary.Kinds[4].Select(x => new RequirementsParserCountableView(x.ParsedAliases)).ToArray(),
            vocabulary.Kinds[5].Select(x => new RequirementsParserSoulView(x.ParsedAliases)).ToArray(),
            vocabulary.Kinds[1].Select(x => new RequirementsParserCrestView(x.ParsedAliases)).ToArray(),
            vocabulary.Kinds[2].Select(x => new RequirementsParserMethodView(x.ParsedAliases)).ToArray());
    }
    private void InstallParserContext()
    {
        var owner = nativeOwner;
        if (owner is null || !Current) return;
        if (!ReferenceEquals(appliedParserOwner, owner) || appliedParserContext is null || !ParserContextMatches(appliedParserContext))
        {
            using var timing = OperationTiming.Start("room.parser.reconstruct-install", parent: readinessTiming?.ActiveId ?? 0);
            var next = ParserContext();
            owner.InvokeVoid("setRequirementsContext", next);
            if (!Current || !ReferenceEquals(nativeOwner, owner)) return;
            appliedParserContext = next;
            appliedParserOwner = owner;
            timing.End();
        }
        // Markup can mount or replace highlight children independently of native
        // value installation and recognition changes. Do not force a context install.
        if (Current && ReferenceEquals(nativeOwner, owner)) owner.InvokeVoid("reconcileMountedRequirements");
    }
    private bool ParserContextMatches(RequirementsParserContextView context)
    {
        var vocabulary = Catalogue.Vocabulary ?? throw new InvalidOperationException("The confirmed requirement vocabulary is not ready.");
        var rooms = Catalogue.PresentationRoomIds.Select(id => Catalogue.Rooms[id]).Where(x => !x.Header.Observation.IsArchived);
        return rooms.Select(x => (x.Observation.RoomId, x.Header.Values.ReferenceId))
                .SequenceEqual(context.Rooms.Select(x => (x.RoomId, x.ReferenceId)))
            && rooms.SelectMany(room => room.Checks.Where(x => !x.Observation.IsArchived).Select(x => (room.Observation.RoomId, x.Values.FriendlyName)))
                .SequenceEqual(context.Checks.Select(x => (x.RoomId, x.FriendlyName)))
            && vocabulary.Kinds[0].Select(x => x.Value.InputSyntax).SequenceEqual(context.Predicates.Select(x => x.InputSyntax))
            && ParserAliasesMatch(vocabulary.Kinds[0], context.Predicates.Select(x => x.Aliases))
            && ParserAliasesMatch(vocabulary.Kinds[3], context.Items.Select(x => x.Aliases))
            && ParserAliasesMatch(vocabulary.Kinds[4], context.Countables.Select(x => x.Aliases))
            && ParserAliasesMatch(vocabulary.Kinds[5], context.Souls.Select(x => x.Aliases))
            && ParserAliasesMatch(vocabulary.Kinds[1], context.Crests.Select(x => x.Aliases))
            && ParserAliasesMatch(vocabulary.Kinds[2], context.Methods.Select(x => x.Aliases));
    }
    private static bool ParserAliasesMatch(IEnumerable<ConfirmedDefinition> definitions, IEnumerable<IReadOnlyList<string>> installed)
    {
        using var aliases = installed.GetEnumerator();
        foreach (var definition in definitions)
            if (!aliases.MoveNext() || !definition.ParsedAliases.SequenceEqual(aliases.Current)) return false;
        return !aliases.MoveNext();
    }
    private async Task OpenGuideAsync()
    {
        if (InspectionBlocked || guide is not null || guideLoading || nativeOwner is null) return;
        if (!TransferKeyboardBeforeAction()) return;
        try
        {
            // Opening a read-only guide does not synthesize a save boundary or
            // wait for transport. Genuine native blur remains independently owned.
            if (InspectionBlocked) return;
            guideLoading = true; StateHasChanged();
            var native = nativeOwner.Invoke<RoomEditing.RoomNativeField[]>("snapshotRoomNative");
            var interaction = nativeOwner.Invoke<long>("roomInteractionGeneration");
            var reference = await Catalogue.LoadReferenceAsync(() => Current && guideLoading && localModal is null, lifetime.Token);
            if (!Current || reference is null || localModal is not null || !Shell.CanUseRoom || workspace.AuthoringBlocked
                || interaction != nativeOwner.Invoke<long>("roomInteractionGeneration") || !native.SequenceEqual(nativeOwner.Invoke<RoomEditing.RoomNativeField[]>("snapshotRoomNative"))) return;
            guide = reference; guideFocusPending = true;
        }
        catch (Exception exception) { if (Current) await Shell.ReportRoomErrorAsync(exception, "The syntax guide could not be loaded. Room work has been retained."); }
        finally { guideLoading = false; if (Current) { StateHasChanged(); await Shell.DrainRoomRefreshAsync(); } }
    }
    private async Task CloseGuideAsync()
    {
        if (!Shell.CanInteract || guide is null || Shell.GeneralErrorOpen) return;
        guide = null; guideFocusPending = false;
        if (nativeOwner is not null) pendingBoundaryFocus = ("room-syntax-guide", nativeOwner.Invoke<long>("roomInteractionGeneration"));
        StateHasChanged();
        await Shell.DrainRoomRefreshAsync();
    }
    private async Task ManageRequirementsAsync()
    {
        if (!Shell.CanInteract || guide is null || Shell.GeneralErrorOpen) return;
        guide = null; guideFocusPending = false;
        StateHasChanged();
        // Application departure still consults the actual queue and native values.
        // Closing a reference modal is never permission to discard pending work.
        if (!DepartureBlocked) Navigation.NavigateTo("/requirements/predicates");
        await Task.CompletedTask;
    }
}
