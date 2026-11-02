using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

public sealed partial class EllaiGuidanceProposalAdmission
{
    /// <summary>Verifies private intake ownership using the existing authority, then atomically
    /// saves receipt evidence and progress. Never submits a report or executes an action.</summary>
    public async Task<EllaiGuidanceProgressDto> CompleteReceiptStepAsync(string sessionId,
        EllaiGuidanceReceiptRequest request, ReadOnlyMemory<byte> receiptProof,
        EllaiUiEntitlements authority, IIntakeSessionEndpointService intake, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProposalId) || request.ProposalId.Length > 512 ||
            string.IsNullOrWhiteSpace(request.PlanId) || request.PlanId.Length > 120 || request.ExpectedVersion < 1 ||
            string.IsNullOrWhiteSpace(request.StepId) || request.StepId.Length > 120 ||
            request.ReceiptId == Guid.Empty || request.ClientSubmissionId == Guid.Empty || receiptProof.Length != 32)
            return new("InvalidRequest");
        var admitted = await AdmitAsync(sessionId, request.ProposalId, authority, ct).ConfigureAwait(false);
        if (admitted.Status != "Ready" || admitted.Catalogue is null) return new(admitted.Status);
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != "Active" || session.OwnerActorId != authority.OwnerActorId ||
            session.ScopeEpoch != authority.ScopeEpoch || session.InteractionEpoch != admitted.InteractionEpoch) return new("Stale");
        var saved = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
        if (saved?.Plan?.PlanId != request.PlanId || saved.ProposalId != request.ProposalId ||
            saved.ScopeEpoch != authority.ScopeEpoch || saved.StartedAtUtc is null ||
            saved.InteractionEpoch > session.InteractionEpoch) return new("Stale");
        var step = saved.Plan.Steps.SingleOrDefault(item => item.StepKey == request.StepId);
        var descriptor = admitted.Catalogue.Descriptors.SingleOrDefault(item => item.ControlId == step?.ControlId);
        if (step?.ControlId is not ("report.submit" or "report.receipt") ||
            descriptor?.FeatureId != EllaiReportUiManifest.FeatureId || descriptor.ProofPolicyId != "intake-receipt-accepted")
            return new("EvidenceRequired");
        if (saved.IntakeEvidence is { } bound &&
            (bound.ReceiptId != request.ReceiptId || bound.ClientSubmissionId != request.ClientSubmissionId)) return new("Stale");
        if (!saved.CompletedStepIds.Contains(request.StepId) &&
            (saved.Version != request.ExpectedVersion || saved.CurrentStepId != request.StepId || saved.Status != "Ready"))
            return new("Conflict");
        var receipt = await intake.GetReceiptAsync(request.ReceiptId, receiptProof, ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (receipt is null || receipt.ReceiptId != request.ReceiptId || receipt.ClientSubmissionId != request.ClientSubmissionId ||
            receipt.ReceivedAtUtc < saved.StartedAtUtc || receipt.ReceivedAtUtc > now) return new("EvidenceRequired");
        if (saved.CompletedStepIds.Contains(request.StepId))
            return new(saved.Status, saved.Version, saved.CurrentStepId, saved.CompletedStepIds, saved.SkippedStepIds);
        var next = saved with
        {
            Version = saved.Version + 1,
            InteractionEpoch = session.InteractionEpoch,
            CurrentStepId = step.NextStepKey,
            CompletedStepIds = [.. saved.CompletedStepIds, step.StepKey],
            IntakeEvidence = new(receipt.ReceiptId, receipt.ClientSubmissionId, receipt.ReceivedAtUtc, now),
            Status = step.NextStepKey is null ? "Completed" : "Ready",
        };
        var updated = await sessions.TryUpdateAsync(session with
        {
            SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(next),
            UpdatedAtUtc = now,
        }, session.SessionVersion, ct).ConfigureAwait(false);
        return updated is null ? new("Conflict") : new(next.Status, next.Version, next.CurrentStepId, next.CompletedStepIds, next.SkippedStepIds);
    }
}
