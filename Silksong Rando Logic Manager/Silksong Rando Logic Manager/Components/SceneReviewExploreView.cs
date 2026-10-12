using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components;

// Modal-local presentation inputs. Drafts are the parent's existing transient
// drafts, not a second draft model; the child only emits typed mutation intents.
public sealed record SceneReviewExploreView(
    SceneDumpReview Review,
    int ClassificationRevision,
    IReadOnlySet<Guid> OriginalCandidates,
    IReadOnlyDictionary<Guid, SceneDumpClassification> OriginalClassifications,
    IReadOnlyList<SceneImportObjectMatch> Matches,
    IReadOnlyDictionary<SceneDumpClassification, IReadOnlyList<SceneImportSelectableRecord>> Records,
    IReadOnlyDictionary<Guid, SceneReviewImportDraft> Drafts,
    IReadOnlyDictionary<Guid, SceneReviewPoint> Points,
    SceneReviewBounds? Frame,
    string? ImageUrl);

public sealed record SceneReviewClassificationChange(SceneDumpReview Review, SceneDumpObject Node, SceneDumpClassification Classification);
public sealed record SceneReviewDispositionChange(SceneDumpReview Review, SceneDumpObject Node, Guid? RecordId);
