namespace SwiftAid.Contracts.Ellai;

/// <summary>User-facing Ellai interaction mode. Determines allowed reads and
/// presentation style; never confers additional authority. (ELL-T02)</summary>
public enum EllaiMode
{
    Ask,
    ShowMe,
    GuideMe,
    HelpMeWork,
    ExploreBranch,
    ReviewEvidence
}

/// <summary>Top-level decision kind for the EllaiDecision schema v2 response profile.</summary>
public enum DecisionKind
{
    Answer,
    NeedInput,
    ProposeUi,
    ProposePlan,
    NoAction,
    Stop
}

/// <summary>Evidence classification for every factual operational claim.</summary>
public enum ClaimKind
{
    Observed,
    DerivedAdministrative,
    ProductFact,
    Historical,
    Unknown,
    Proposed,
    Simulation
}

/// <summary>Semantic UI intent kinds Ellai may propose. Proposals only;
/// the gateway converts validated proposals into expiring client intents.</summary>
public enum UiIntentKind
{
    NavigateFeature,
    OpenAuthorizedRecord,
    SelectTab,
    SetPermittedFilter,
    HighlightControl,
    ShowEvidence,
    PrepareDemoDraft,
    YieldControl,
    StartGuidance,
    ControlGuidance
}

/// <summary>Allowed administrative effect kinds. All other writes are human-owned (R3) or excluded (R5).</summary>
public enum AdminActionKind
{
    CreateAdministrativeReviewTask,
    RecordAssignmentRequest,
    EnqueueInternalReminder
}

/// <summary>Reference to versioned authorized evidence backing a claim.</summary>
public sealed record EvidenceRef(
    string SourceKind,
    string ResourceId,
    string Version,
    string? FieldPath,
    string EvidenceId);

/// <summary>Single grounded claim with its evidence links.</summary>
public sealed record GroundedClaim(
    string ClaimId,
    string Text,
    ClaimKind Kind,
    IReadOnlyList<EvidenceRef> Evidence);

/// <summary>Clarifying question; displayed at most three at once.</summary>
public sealed record EllaiQuestion(
    string QuestionId,
    string Text,
    IReadOnlyList<string> ChoiceIds);

/// <summary>Proposed semantic UI intent. Arguments are validated against a
/// strict per-kind schema at the gateway; this record is the convenient
/// .NET shape, not execution authority.</summary>
public sealed record UiIntentProposal(
    UiIntentKind Kind,
    string FeatureId,
    string? ResourceId,
    string? ControlId,
    IReadOnlyDictionary<string, string> Arguments);

/// <summary>One proposed administrative action within a plan proposal.</summary>
public sealed record AdminActionProposal(
    string ProposalLabel,
    AdminActionKind Kind,
    EvidenceRef Obligation,
    string? RecipientCandidateId,
    DateTimeOffset? HumanSuppliedDueAtUtc,
    IReadOnlyList<EvidenceRef> Evidence,
    string FactualRationale);

/// <summary>Bounded administrative plan proposal: at most three actions.</summary>
public sealed record PlanProposal(
    string Objective,
    IReadOnlyList<AdminActionProposal> Actions);

/// <summary>Ordinary-dialogue response contract, schema version 2.
/// Exactly one response profile is active per turn.</summary>
public sealed record EllaiDecision(
    int SchemaVersion,
    DecisionKind Kind,
    string DisplayText,
    string SpokenText,
    IReadOnlyList<GroundedClaim> Claims,
    IReadOnlyList<EllaiQuestion> Questions,
    IReadOnlyList<UiIntentProposal> UiIntents,
    PlanProposal? PlanProposal,
    IReadOnlyList<string> TopicRefs,
    string? ResumeSuggestion,
    string? StopReason)
{
    public const int CurrentSchemaVersion = 2;
}
