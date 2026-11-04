namespace SwiftAid.Contracts.Ellai;

/// <summary>Guided-experience mode for Bonboarding-presented journeys.</summary>
public enum GuidanceMode
{
    SystemTour,
    Presentation,
    Demo,
    Training,
    Technical,
    Navigation
}

/// <summary>Gate that must be satisfied before advancing past a step.</summary>
public enum GuidanceAdvancePolicy
{
    Manual,
    NarrationThenReady,
    UiObservationRequired,
    LocalPersistenceRequired,
    DomainReceiptRequired,
    HumanApprovalRequired
}

/// <summary>Client-issued guidance control command (creation uses StartGuidanceRequest).</summary>
public enum GuidanceCommandKind
{
    Start,
    Next,
    Previous,
    Jump,
    Pause,
    Resume,
    Stop,
    ReplayNarration,
    Return,
    SwitchMode,
    SkipOptional
}

/// <summary>Strength of completion proof attached to a step visit.</summary>
public enum GuidanceProofKind
{
    ContentSeen,
    ExplanationDelivered,
    UiObserved,
    LocalPersistenceVerified,
    HumanApprovalObserved,
    DomainEffectVerified
}

/// <summary>Coordinator-owned guidance status.</summary>
public enum GuidanceStatus
{
    Preparing,
    WaitingForRoute,
    WaitingForTarget,
    Showing,
    Narrating,
    AwaitingUser,
    HandlingQuestion,
    Detouring,
    Paused,
    Blocked,
    Completed,
    Incomplete,
    Cancelled,
    Stale,
    Failed
}

/// <summary>Trusted guidance position projection; the client cannot grant its own fence/epoch.</summary>
public sealed record GuidancePosition(
    string GuidanceAttemptId,
    string JourneyId,
    string JourneyVersion,
    string ManifestHash,
    string StepId,
    string StepVisitId,
    long GuidanceVersion,
    long ControlFence,
    long InteractionEpoch,
    long SpeechEpoch,
    string? GeneratedPlanId = null,
    int? GeneratedPlanRevision = null,
    string? CatalogueVersion = null,
    string? UiContractVersion = null,
    int? SegmentOrdinal = null);

/// <summary>Client guidance control request. Jump requires only TargetStepId;
/// SwitchMode requires only TargetMode; other commands reject both.</summary>
public sealed record GuidanceControlRequest(
    string ClientCommandId,
    string GuidanceAttemptId,
    long ExpectedGuidanceVersion,
    GuidanceCommandKind Command,
    string? TargetStepId,
    GuidanceMode? TargetMode);

/// <summary>Deterministic completion evidence chosen by the verifier, never the model.</summary>
public sealed record GuidanceCompletionEvidence(
    string ProofId,
    GuidanceProofKind Kind,
    string PredicateId,
    string StepId,
    string StepVisitId,
    IReadOnlyList<EvidenceRef> Sources,
    DateTimeOffset ObservedAtUtc,
    bool Current);

/// <summary>Accepted journey manifest: curated reference or validated generated plan.</summary>
public sealed record EllaiJourneyManifest(
    int SchemaVersion,
    string JourneyId,
    string JourneyVersion,
    string ManifestHash,
    IReadOnlyList<GuidanceMode> AllowedModes,
    string Environment,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ProductFactVersions,
    IReadOnlyList<EllaiGuidanceStep> Steps,
    IReadOnlyList<string> MandatoryObjectiveIds,
    bool OfflineEligible,
    string? CycleGrantReference);

/// <summary>Single guidance step with registered references and proof predicate.</summary>
public sealed record EllaiGuidanceStep(
    string StepId,
    string TopicId,
    string FeatureId,
    string? ControlId,
    string? ResourceBindingKey,
    IReadOnlyList<string> PreparationIds,
    string? ContextPredicate,
    string NarrationSource,
    string NarrationPolicy,
    GuidanceAdvancePolicy AdvancePolicy,
    string ProofPredicate,
    bool Mandatory,
    IReadOnlyList<string> KnownBranchIds,
    string? TargetTimeoutFallback);

/// <summary>Coordinator-issued snapshot of current guidance state.</summary>
public sealed record GuidanceSnapshot(
    GuidancePosition Position,
    GuidanceMode Mode,
    GuidanceStatus Status,
    string? LibraryState,
    string? WaitReason,
    IReadOnlyList<string> PermittedControls,
    string NarrationState,
    string ContentCoverage,
    string TaskCoverage,
    string EffectCoverage,
    long LastAcceptedCommandSequence,
    long LastAcceptedEventSequence);

/// <summary>Saved return point for detour/return; carries no reusable authorization.</summary>
public sealed record GuidanceReturnFrame(
    GuidancePosition ParentPosition,
    GuidanceMode ParentMode,
    string? ResourceRef,
    string? TabRef,
    string? FilterRef,
    string NarrationBoundary,
    IReadOnlyList<string> PendingReceiptRefs,
    IReadOnlyList<string> JourneySourceVersions);

/// <summary>Training objective verification result from the deterministic verifier.</summary>
public sealed record TrainingTaskResult(
    string AttemptId,
    string ObjectiveId,
    string TaskVersion,
    string State,
    GuidanceProofKind ProofKind,
    IReadOnlyList<EvidenceRef> ProofRefs,
    string VerifierVersion,
    IReadOnlyList<string> SourceVersions);
