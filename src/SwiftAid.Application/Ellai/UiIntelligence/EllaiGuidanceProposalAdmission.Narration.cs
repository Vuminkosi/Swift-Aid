using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

public sealed partial class EllaiGuidanceProposalAdmission
{
    /// <summary>Publishes only the current, durably validated step. The client supplies identity,
    /// never narration text. Speech remains ephemeral and uses the ordinary response audio queue.</summary>
    public async Task<EllaiSessionResponseDto?> PrepareNarrationAsync(string sessionId,
        EllaiGuidanceNarrationRequest request, EllaiUiEntitlements authority,
        EllaiSessionResponseStore responses, CancellationToken ct)
        => await PrepareNarrationCoreAsync(sessionId, request, authority, responses, null, null, ct).ConfigureAwait(false);

    public async Task<EllaiSessionResponseDto?> PrepareValidationNarrationAsync(string sessionId,
        EllaiValidationNarrationRequest request, EllaiUiEntitlements authority,
        EllaiSessionResponseStore responses, CancellationToken ct)
    {
        var currentWindow = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() / EllaiValidationNarrationRequest.WindowSeconds;
        if (request is null || request.TimeWindow < currentWindow - 1 || request.TimeWindow > currentWindow + 1 ||
            EllaiValidationPrompts.Resolve(request.ValidationCode) is not { } text)
            return null;
        var guidance = new EllaiGuidanceNarrationRequest(request.ProposalId, request.PlanId, request.StepId,
            request.ExpectedVersion, request.EnglishVoiceId);
        return await PrepareNarrationCoreAsync(sessionId, guidance, authority, responses, text, request.EventId, ct).ConfigureAwait(false);
    }

    private async Task<EllaiSessionResponseDto?> PrepareNarrationCoreAsync(string sessionId,
        EllaiGuidanceNarrationRequest request, EllaiUiEntitlements authority,
        EllaiSessionResponseStore responses, string? validationText, string? validationEventId, CancellationToken ct)
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
        var narration = validationText ?? step.Explanation;
        if (validationText is not null && step.ControlId is not ("report.subject" or "report.narrative" or "report.time-location")) return null;
        if (request.ValidationInputId is not null || request.ObservationContextStamp is not null)
        {
            if (request.ValidationInputId is null || request.ObservationContextStamp is null ||
                !admission.Catalogue.Descriptors.Any(item => item.ControlId == step.ControlId && item.FeatureId == EllaiReportUiManifest.FeatureId) ||
                admission.Observation?.ContextStamp != request.ObservationContextStamp ||
                admission.Observation.ValidationInputIds?.Select(EllaiReportValidationGuidance.Find)
                    .FirstOrDefault(item => item is not null)?.InputId != request.ValidationInputId ||
                EllaiReportValidationGuidance.Find(request.ValidationInputId) is not { } issue ||
                EllaiReportValidationGuidance.ControlFor(request.ValidationInputId) is null) return null;
            narration = issue.Message;
        }
        var turn = (await outbox.ReadConversationAsync(sessionId, authority.OwnerActorId, ct).ConfigureAwait(false))
            .MaxBy(item => item.Sequence);
        if (turn is null || turn.Outcome != "Completed") return null;
        // Fence cancellation, replacement and newer accepted turns before publication.
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current?.SessionVersion != session.SessionVersion || current.InteractionEpoch != session.InteractionEpoch) return null;
        if (request.ValidationInputId is not null)
        {
            var latest = await observations.LatestAsync(sessionId, ct).ConfigureAwait(false);
            if (latest is null || latest.ContextStamp != request.ObservationContextStamp ||
                latest.ValidationInputIds?.Select(EllaiReportValidationGuidance.Find)
                    .FirstOrDefault(item => item is not null)?.InputId != request.ValidationInputId ||
                EllaiReportValidationGuidance.ControlFor(request.ValidationInputId) is null) return null;
        }
        return responses.PublishOrGetProgress(sessionId, turn.ClientUtteranceId, narration,
            new(validationEventId ?? request.EventId, "Guidance", "Guidance", true, session.InteractionEpoch,
                (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(5)), request.EnglishVoiceId);
    }
}
