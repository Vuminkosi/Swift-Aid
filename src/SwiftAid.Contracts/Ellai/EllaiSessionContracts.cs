namespace SwiftAid.Contracts.Ellai;

/// <summary>Investigation lifecycle states (extends base AgentInvestigation).</summary>
public enum EllaiInvestigationStatus
{
    Created,
    GatheringEvidence,
    Reasoning,
    NeedsInput,
    AwaitingApproval,
    Resuming,
    Verifying,
    Completed,
    NoAction,
    Cancelled,
    Expired,
    Failed,
    Stale,
    Partial,
    BudgetExhausted,
    CapabilityUnavailable
}

/// <summary>Surface placement for the single compact composer.</summary>
public enum EllaiSurfacePlacement
{
    Floating,
    Attached,
    Inactive
}

/// <summary>Capability readiness state for the Settings/About projection.</summary>
public enum CapabilityReadiness
{
    Unconfigured,
    ConfiguredUnverified,
    VerifiedAvailable,
    Degraded,
    Unavailable,
    PolicyBlocked,
    Disabled
}

/// <summary>Client session-start request. Carries no trusted tenant/role.</summary>
public sealed record StartSessionRequest(
    EllaiMode Mode,
    string ClientRequestId,
    string? FeatureId,
    string? ScenarioId,
    string? InputPreference,
    string? OutputPreference);

/// <summary>Server-issued session view projection.</summary>
public sealed record EllaiSessionView(
    string SessionId,
    EllaiMode Mode,
    string? ActiveInvestigationId,
    string Status,
    string ScopeDisplay,
    string ConsentVersion,
    string CapabilityVersion,
    int CyclesUsed,
    int CyclesAllowed,
    string? ControlOwner,
    long LastEventSequence,
    long SessionVersion);

/// <summary>Finalized user utterance submission. Optional
/// <c>FeatureId</c>/<c>ResourceId</c> scope background reasoning to the
/// client's current feature; absent features default to "general" and every
/// value still passes policy, consent and budget checks.</summary>
public sealed record SubmitUtteranceRequest(
    string ClientUtteranceId,
    string SessionId,
    long ExpectedSessionVersion,
    string FinalizedText,
    string? TranscriptRef,
    string InputSource,
    long ClientEpoch,
    string? FeatureId = null,
    string? ResourceId = null,
    EllaiLanguagePreferences? Languages = null);

/// <summary>Client UI-intent execution envelope; server-issued and expiring.</summary>
public sealed record UiIntentEnvelope(
    string IntentId,
    string SessionId,
    long ControlFence,
    string FeatureId,
    UiIntentKind Kind,
    IReadOnlyDictionary<string, string> Arguments,
    string ExpectedUiVersion,
    string ExpectedAppVersion,
    DateTimeOffset ExpiresAtUtc,
    string AuditCorrelationId);

/// <summary>UI execution observation; never authoritative business proof.</summary>
public sealed record UiObservation(
    string IntentId,
    long ControllerEpoch,
    string Status,
    string? Route,
    string? ControlState,
    string? ResourceVersion);

/// <summary>Redacted activity event for the evidence timeline.</summary>
public sealed record EllaiActivityEvent(
    string EventId,
    string SessionId,
    string? RunId,
    string? CycleId,
    string? TurnId,
    long Sequence,
    string Kind,
    string Status,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> ApprovalRefs,
    IReadOnlyList<string> ReceiptRefs,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    int SchemaVersion);

/// <summary>Voice capability descriptor for the Settings/About surface.</summary>
public sealed record VoiceCapability(
    string Engine,
    string Model,
    string Modality,
    string Language,
    bool LocalOnlyVerified,
    IReadOnlyList<string> SupportedControls,
    string? VerificationBuild,
    string? VerificationDevice,
    DateTimeOffset? VerifiedAtUtc,
    string Availability,
    string? Reason);
