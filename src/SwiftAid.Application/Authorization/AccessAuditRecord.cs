namespace SwiftAid.Application.Authorization;

/// <summary>Minimized audit evidence for a denied protected resource decision.</summary>
public sealed record AccessAuditRecord(
    string ActorId,
    Guid ResourceId,
    CaseOperation Operation,
    AccessDecisionCode DecisionCode,
    DateTimeOffset RecordedAtUtc,
    long? ResourceVersion,
    long AccessEpoch);
