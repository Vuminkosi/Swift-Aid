using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Surveillance;

public sealed record SignalDossier(Guid SignalId, long Version, Guid AssessmentId, string State,
    DetectionAssessment Assessment, IReadOnlyList<SignalReviewDecision> Decisions);
public sealed record SignalReviewDecision(Guid CommandId, Guid AssessmentId, long Version, string ActorId,
    string State, string Rationale, DateTimeOffset RecordedAtUtc);
public sealed record SignalDecisionCommand(Guid CommandId, Guid SignalId, long ExpectedVersion,
    Guid AssessmentId, string State, string Rationale);
public sealed record SignalDecisionResult(string Outcome, long? Version = null);

public interface ISignalReviewService
{
    bool Enabled { get; }
    Task<IReadOnlyList<SignalDossier>?> ForCaseAsync(ActorContext actor, Guid caseId, CancellationToken ct);
    Task<SignalDossier?> GetAsync(ActorContext actor, Guid signalId, CancellationToken ct);
    Task<SignalDecisionResult> DecideAsync(SignalDecisionCommand command, ActorContext actor, CancellationToken ct);
}
