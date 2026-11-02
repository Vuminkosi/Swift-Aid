using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

public sealed partial class EllaiGuidanceProposalAdmission
{
    /// <summary>Publishes only the current, durably validated step. The client supplies identity,
    /// never narration text. Speech remains ephemeral and uses the ordinary response audio queue.</summary>
    public async Task<EllaiSessionResponseDto?> PrepareNarrationAsync(string sessionId,
        EllaiGuidanceNarrationRequest request, EllaiUiEntitlements authority,
        EllaiSessionResponseStore responses, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProposalId) || request.ProposalId.Length > 512 ||
            string.IsNullOrWhiteSpace(request.PlanId) || request.PlanId.Length > 120 ||
            string.IsNullOrWhiteSpace(request.StepId) || request.StepId.Length > 120 || request.ExpectedVersion < 1 ||
            !EllaiLanguagePreferences.IsSupportedEnglishVoice(request.EnglishVoiceId)) return null;
        var admission = await AdmitAsync(sessionId, request.ProposalId, authority, ct).ConfigureAwait(false);
        if (admission.Status != "Ready") return null;
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != "Active" || session.OwnerActorId != authority.OwnerActorId ||
            session.ScopeEpoch != authority.ScopeEpoch || session.InteractionEpoch != admission.InteractionEpoch) return null;
        var checkpoint = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
        if (checkpoint is not { Status: "Ready", Plan: not null } || checkpoint.ProposalId != request.ProposalId ||
            checkpoint.Plan.PlanId != request.PlanId || checkpoint.Version != request.ExpectedVersion ||
            checkpoint.CurrentStepId != request.StepId || checkpoint.ScopeEpoch != authority.ScopeEpoch) return null;
        var step = checkpoint.Plan.Steps.SingleOrDefault(item => item.StepKey == request.StepId);
        if (step is null || !admission.Catalogue!.Descriptors.Any(item => item.ControlId == step.ControlId)) return null;
        var turn = (await outbox.ReadConversationAsync(sessionId, authority.OwnerActorId, ct).ConfigureAwait(false))
            .MaxBy(item => item.Sequence);
        if (turn is null || turn.Outcome != "Completed") return null;
        // Fence cancellation, replacement and newer accepted turns before publication.
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current?.SessionVersion != session.SessionVersion || current.InteractionEpoch != session.InteractionEpoch) return null;
        var existing = responses.ReadSince(sessionId, 0, EllaiSessionResponseStore.MaximumResponsesPerSession)
            .Responses.SingleOrDefault(item => item.Progress?.EventId == request.EventId);
        if (existing is not null) return existing;
        return responses.PublishProgress(sessionId, turn.ClientUtteranceId, step.Explanation,
            new(request.EventId, "Guidance", "Guidance", true, session.InteractionEpoch,
                (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(5)), request.EnglishVoiceId);
    }
}
