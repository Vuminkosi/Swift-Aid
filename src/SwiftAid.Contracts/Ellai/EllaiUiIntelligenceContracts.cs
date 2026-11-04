namespace SwiftAid.Contracts.Ellai;

/// <summary>Risk class for a registered control. R3/R5 are never agent-executable.</summary>
public enum UiRiskClass
{
    R0,
    R1,
    R2,
    R3,
    R4,
    R5
}

/// <summary>Live observation state of a registered control. Unknown means
/// not yet observed, never unavailable-forever.</summary>
public enum UiObservationState
{
    Unknown,
    Unmounted,
    Hidden,
    Disabled,
    Ready,
    Ambiguous,
    Stale
}

/// <summary>Step behavior in a generated guidance plan chunk.</summary>
public enum PlanStepBehavior
{
    Explain,
    GuideHuman,
    RequestUiIntent,
    HandOff
}

/// <summary>Planning-draft status for the GuidancePlanV1 response profile.</summary>
public enum GuidancePlanStatus
{
    Ready,
    NeedsInput,
    Blocked,
    UnsupportedGoal
}

/// <summary>Versioned semantic descriptor for one registered control/objective.</summary>
public sealed record UiControlDescriptor(
    string ControlId,
    string FeatureId,
    int DescriptorVersion,
    string UiContractVersion,
    string OwningModule,
    string ElementKind,
    string LabelResourceKey,
    string DescriptionResourceKey,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> GoalTags,
    IReadOnlyList<string> ProductFactIds,
    string NavigationId,
    string AnchorBindingKey,
    IReadOnlyList<string> AllowedIntentBindings,
    UiRiskClass RiskClass,
    IReadOnlyList<GuidanceMode> SupportedModes,
    IReadOnlyList<string> PrerequisiteIds,
    IReadOnlyList<string> SuccessorIds,
    string? ObjectiveId,
    string? ProofPolicyId,
    IReadOnlyList<string> RequiredCapabilities,
    string DataClassification,
    bool HumanConfirmationRequired)
{
    /// <summary>Application-authored semantics, never current field values or model instructions.</summary>
    public EllaiUiSemantics? Semantics { get; init; }

    /// <summary>Trusted planning metadata only: a later human step may be
    /// disabled until its declared prerequisites are completed. Never permits
    /// activation or completion while the control is disabled.</summary>
    public bool AllowDeferredEnablement { get; init; }
}

/// <summary>Client-posted mounted-control report: the live UI observation
/// channel input. The server stamps receipt time; freshness derives from
/// that stamp, never from client claims.</summary>
public sealed record UiObservationReport(
    string ViewInstanceId,
    string ContextStamp,
    string Platform,
    IReadOnlyList<string> MountedControlIds,
    IReadOnlyList<UiControlState>? ControlStates = null,
    string? CurrentNavigationId = null,
    IReadOnlyList<string>? CompletedControlIds = null)
{
    public bool IsValid() =>
        (CompletedControlIds is null || CompletedControlIds.Count <= 20 &&
            CompletedControlIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 120) &&
            CompletedControlIds.Distinct(StringComparer.Ordinal).Count() == CompletedControlIds.Count) &&
        (CurrentNavigationId is null || CurrentNavigationId == EllaiReportUiManifest.NavigationId ||
            EllaiNavigationManifest.ResolveRoute(CurrentNavigationId) is not null) &&
        !string.IsNullOrWhiteSpace(ViewInstanceId) && ViewInstanceId.Length <= 120 &&
        !string.IsNullOrWhiteSpace(ContextStamp) && ContextStamp.Length <= 120 &&
        !string.IsNullOrWhiteSpace(Platform) && Platform.Length <= 40 &&
        MountedControlIds is { Count: <= 20 } &&
        MountedControlIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 120) &&
        MountedControlIds.Distinct(StringComparer.Ordinal).Count() == MountedControlIds.Count &&
        (ControlStates is null || ControlStates.Count <= 20 &&
            ControlStates.All(state => state is not null && MountedControlIds.Contains(state.ControlId) && Enum.IsDefined(state.State)) &&
            ControlStates.Select(state => state.ControlId).Distinct(StringComparer.Ordinal).Count() == ControlStates.Count);
}

/// <summary>Presentation evidence only; never grants authorization or proves a business action.</summary>
public sealed record UiControlState(string ControlId, UiObservationState State, bool CompletionAccepted = false);

/// <summary>Bounded timestamped observation of a registered control.</summary>
public sealed record UiControlObservation(
    string ControlId,
    int DescriptorVersion,
    string ViewInstanceId,
    string ContextStamp,
    UiObservationState State,
    string Platform,
    DateTimeOffset ObservedAtUtc,
    string? SemanticContext);

/// <summary>Single step in a generated guidance plan draft (data only).</summary>
public sealed record GuidancePlanStep(
    string StepKey,
    string ControlId,
    string ObjectiveId,
    PlanStepBehavior Behavior,
    string? UiIntentBindingId,
    string? ResourceBindingKey,
    IReadOnlyList<string> FactIds,
    IReadOnlyList<string> EvidenceIds,
    string Explanation,
    string? NextStepKey)
{
    public IReadOnlyList<string> SuggestionIds { get; init; } = [];
}

/// <summary>A preplanned, registered objective offered as a new conversational request, never an executable action.</summary>
public sealed record GuidanceStepSuggestion(string Label, string ControlId, string ObjectiveId);

/// <summary>Dynamic-guidance response contract draft, schema version 1.
/// The application validator decides whether it becomes eligible.</summary>
public sealed record EllaiGuidancePlanDraft(
    int SchemaVersion,
    GuidancePlanStatus Status,
    string Objective,
    GuidanceMode Mode,
    IReadOnlyList<GuidancePlanStep> Steps,
    IReadOnlyList<EllaiQuestion> Questions,
    string? StopReason)
{
    public IReadOnlyDictionary<string, GuidanceStepSuggestion> Suggestions { get; init; } = new Dictionary<string, GuidanceStepSuggestion>();

    public const int CurrentSchemaVersion = 1;

    public const int MaxStepsPerChunk = 8;

    public const int MaxExplanationChars = 1000;
}

/// <summary>Accepted immutable plan revision; assigned by the application, never the model.</summary>
public sealed record ValidatedGuidancePlan(
    string PlanId,
    int Revision,
    string? ParentRevisionId,
    string Reason,
    IReadOnlyList<string> AddedStepIds,
    IReadOnlyList<string> RemovedStepIds,
    IReadOnlyList<string> MovedStepIds,
    IReadOnlyList<GuidancePlanStep> Steps,
    IReadOnlyList<string> RemainingMandatoryObjectiveIds,
    string CatalogueVersion,
    string UiContractVersion)
{
    public IReadOnlyDictionary<string, GuidanceStepSuggestion> Suggestions { get; init; } = new Dictionary<string, GuidanceStepSuggestion>();
    public IReadOnlyDictionary<string, IReadOnlyList<EllaiUiInputDescriptor>> InputsByStep { get; init; }
        = new Dictionary<string, IReadOnlyList<EllaiUiInputDescriptor>>();
}
