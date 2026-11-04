using SwiftAid.Contracts.Ellai;
using SwiftAid.Application.Abstractions;

namespace SwiftAid.Application.Ellai;

/// <summary>Server-resolved grants. Never bind this record from an HTTP request or model output.</summary>
public sealed record EllaiUiEntitlements(string OwnerActorId, string ScopeEpoch,
    IReadOnlySet<string> Features, IReadOnlySet<string> Capabilities);

public sealed record EllaiGuidanceAdmission(string Status, string? Objective = null,
    UiRegistrySnapshot? Catalogue = null, StoredUiObservationReport? Observation = null,
    long InteractionEpoch = 0);

/// <summary>Resolves a durable proposal and fences it against session ownership, scope,
/// newer turns and current UI evidence before the existing model planner is invoked.</summary>
public sealed partial class EllaiGuidanceProposalAdmission(
    IEllaiSessionStore sessions, IEllaiUtteranceOutboxStore outbox,
    EllaiUiRegistry registry, IEllaiUiObservationStore observations, TimeProvider? clock = null,
    ICacheStore? templateCache = null)
{
    /// <summary>Leases generation inside the existing durable session and saves only
    /// a freshly validated plan. Retries reuse an accepted plan; concurrent calls do not bill twice.</summary>
    public async Task<EllaiGuidancePlanningResult> PlanAsync(string sessionId, string proposalId,
        EllaiUiEntitlements authority, EllaiModelGuidancePlanner planner,
        IEllaiUiDiscoveryService discovery, EllaiGuidancePlanValidator validator,
        EllaiUsageLedger ledger, IEllaiUsageStore usage, CancellationToken ct)
    {
        var admitted = await AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        if (admitted.Status != "Ready" || admitted.Observation is null || admitted.Catalogue is null)
            return new(admitted.Status);
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != "Active" || session.OwnerActorId != authority.OwnerActorId
            || session.ScopeEpoch != authority.ScopeEpoch || session.InteractionEpoch != admitted.InteractionEpoch)
            return new("Stale");
        var previous = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
        var sameProposal = previous?.ProposalId == proposalId && previous.ScopeEpoch == authority.ScopeEpoch
            && previous.InteractionEpoch <= admitted.InteractionEpoch;
        var sameContext = sameProposal && previous!.ContextStamp == admitted.Observation.ContextStamp
            && previous.InteractionEpoch == admitted.InteractionEpoch;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (sameContext && previous!.Status == "Generating" && previous.LeaseExpiresAtUtc > now)
            return new("Generating");
        if (sameProposal && previous!.Status is "Cancelled" or "Completed")
            return new(previous.Status);
        if (sameProposal && previous!.Status is "Ready" or "Paused" && previous.Plan is { } existing)
        {
            var observed = discovery.Observe(admitted.Catalogue,
                admitted.Observation.MountedControlIds.ToHashSet(StringComparer.Ordinal),
                admitted.Observation.ViewInstanceId, admitted.Observation.ContextStamp,
                admitted.Observation.Platform, admitted.Observation.ReceivedAtUtc, admitted.Observation.ControlStates);
            var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, admitted.Objective!,
                GuidanceMode.Navigation, existing.Steps, [], null) { Suggestions = existing.Suggestions };
            var accepted = validator.ValidateObserved(draft, new(admitted.Catalogue.Descriptors[0].FeatureId, [], 20),
                authority.Features, authority.Capabilities, observed, admitted.Observation.ViewInstanceId,
                admitted.Observation.ContextStamp, now, existing.PlanId, previous.CurrentStepId,
                previous.CompletedStepIds.ToHashSet(StringComparer.Ordinal), admitted.Observation.CompletedControlIds?.ToHashSet(StringComparer.Ordinal), previous.SkippedStepIds.ToHashSet(StringComparer.Ordinal));
            if (accepted.Plan is not null && existing.CatalogueVersion == admitted.Catalogue.CatalogueVersion
                && existing.UiContractVersion == admitted.Catalogue.UiContractVersion)
            {
                var normalized = existing with { Steps = accepted.Plan.Steps, InputsByStep = accepted.Plan.InputsByStep };
                var sameInputs = existing.InputsByStep.Count == normalized.InputsByStep.Count &&
                    normalized.InputsByStep.All(pair => existing.InputsByStep.TryGetValue(pair.Key, out var inputs) &&
                        inputs.SequenceEqual(pair.Value));
                if (!sameContext || !existing.Steps.SequenceEqual(normalized.Steps) || !sameInputs)
                {
                    var rebound = previous with { ContextStamp = admitted.Observation.ContextStamp,
                        InteractionEpoch = admitted.InteractionEpoch, Version = previous.Version + 1, Plan = normalized };
                    var restored = await sessions.TryUpdateAsync(session with
                    {
                        SessionVersion = session.SessionVersion + 1,
                        GuidanceJson = EllaiGuidanceCheckpoint.Serialize(rebound),
                        UpdatedAtUtc = now,
                    }, session.SessionVersion, ct).ConfigureAwait(false);
                    if (restored is null) return new("Conflict");
                }
                return new(previous.Status, normalized);
            }
        }
        var attempt = sameProposal ? previous!.GenerationAttempt + 1 : 1;
        if (attempt > EllaiGuidancePlanValidator.MaxReplanDispatches) return new("ReplanLimit");
        var checkpoint = new EllaiGuidanceCheckpoint(proposalId, admitted.Observation.ContextStamp,
            authority.ScopeEpoch, admitted.InteractionEpoch, (previous?.Version ?? 0) + 1,
            Guid.NewGuid().ToString("N"), "Generating", now.AddMinutes(2), null, null, [], [],
            admitted.Catalogue.Descriptors[0].NavigationId) { GenerationAttempt = attempt, FeatureId = admitted.Catalogue.Descriptors[0].FeatureId, StartedAtUtc = sameProposal ? previous!.StartedAtUtc ?? now : now,
                IntakeEvidence = sameProposal ? previous!.IntakeEvidence : null };
        var claimed = await sessions.TryUpdateAsync(session with
        {
            SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint),
            UpdatedAtUtc = now,
        }, session.SessionVersion, ct).ConfigureAwait(false);
        if (claimed is null) return new("Conflict");

        EllaiGuidancePlanningResult generated;
        try
        {
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            generation.CancelAfter(TimeSpan.FromSeconds(90));
            generated = await GeneratePlanAsync(sessionId, proposalId, authority, planner, discovery,
                validator, ledger, usage, generation.Token).ConfigureAwait(false);
        }
        catch
        {
            // Release only this lease. Never overwrite a later user command or generation.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await sessions.TryUpdateAsync(claimed with
            {
                SessionVersion = claimed.SessionVersion + 1,
                GuidanceJson = EllaiGuidanceCheckpoint.Serialize(checkpoint with
                {
                    Version = checkpoint.Version + 1, Status = "Failed",
                }),
            }, claimed.SessionVersion, cleanup.Token).ConfigureAwait(false);
            throw;
        }

        if (generated.Plan is not null)
            generated = generated with { Plan = generated.Plan with { PlanId = "plan-" + checkpoint.GenerationId } };

        var next = checkpoint with
        {
            Version = checkpoint.Version + 1,
            Status = generated.Plan is null ? "Failed" : "Ready",
            Plan = generated.Plan,
            CurrentStepId = generated.Plan?.Steps[0].StepKey,
            NavigationId = generated.Plan is null ? checkpoint.NavigationId :
                admitted.Catalogue.Descriptors.Single(descriptor => descriptor.ControlId == generated.Plan.Steps[0].ControlId).NavigationId,
        };
        var saved = await sessions.TryUpdateAsync(claimed with
        {
            SessionVersion = claimed.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(next),
            UpdatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow(),
        }, claimed.SessionVersion, ct).ConfigureAwait(false);
        return saved is null ? new("Stale") : generated;
    }

    private async Task<EllaiGuidancePlanningResult> GeneratePlanAsync(string sessionId, string proposalId,
        EllaiUiEntitlements authority, EllaiModelGuidancePlanner planner,
        IEllaiUiDiscoveryService discovery, EllaiGuidancePlanValidator validator,
        EllaiUsageLedger ledger, IEllaiUsageStore usage, CancellationToken ct)
    {
        var admitted = await AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        if (admitted.Status != "Ready" || admitted.Catalogue is null || admitted.Observation is null)
            return new(admitted.Status);
        var observed = discovery.Observe(admitted.Catalogue,
            admitted.Observation.MountedControlIds.ToHashSet(StringComparer.Ordinal),
            admitted.Observation.ViewInstanceId, admitted.Observation.ContextStamp,
            admitted.Observation.Platform, admitted.Observation.ReceivedAtUtc, admitted.Observation.ControlStates);
        var planningSession = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        var planningCheckpoint = EllaiGuidanceCheckpoint.Deserialize(planningSession?.GuidanceJson);
        var reportAlreadyReceived = planningCheckpoint?.ProposalId == proposalId && planningCheckpoint.IntakeEvidence is not null;
        var feature = admitted.Catalogue.Descriptors[0].FeatureId;
        // Organization-specific report rules are not yet represented in this authority snapshot.
        // Do not share authenticated report templates until their policy scope is available.
        var shareable = feature != EllaiReportUiManifest.FeatureId || !authority.Features.Contains(EllaiNavigationManifest.FeatureId);
        var cacheKey = shareable ? EllaiSharedGuidanceTemplates.Key(admitted.Objective!, admitted.Catalogue, authority, observed, reportAlreadyReceived) : null;
        var planId = "plan-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] { sessionId, proposalId, admitted.Observation.ContextStamp })))).ToLowerInvariant();
        if (cacheKey is not null)
        {
            var cached = await EllaiSharedGuidanceTemplates.ReadAsync(templateCache, cacheKey, admitted.Objective!, admitted.Catalogue, ct).ConfigureAwait(false);
            if (cached is not null && (!reportAlreadyReceived || cached.Steps.All(step => step.ControlId != "report.submit")))
            {
                var refreshed = await AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
                if (refreshed.Status != "Ready" || refreshed.InteractionEpoch != admitted.InteractionEpoch ||
                    refreshed.Observation?.ViewInstanceId != admitted.Observation.ViewInstanceId ||
                    refreshed.Observation?.ContextStamp != admitted.Observation.ContextStamp) return new("Stale");
                var freshObserved = discovery.Observe(refreshed.Catalogue!, refreshed.Observation.MountedControlIds.ToHashSet(StringComparer.Ordinal),
                    refreshed.Observation.ViewInstanceId, refreshed.Observation.ContextStamp, refreshed.Observation.Platform,
                    refreshed.Observation.ReceivedAtUtc, refreshed.Observation.ControlStates);
                var cachedValidation = validator.ValidateObserved(cached, new(feature, [], 20), authority.Features, authority.Capabilities,
                    freshObserved, refreshed.Observation.ViewInstanceId, refreshed.Observation.ContextStamp,
                    (clock ?? TimeProvider.System).GetUtcNow(), planId);
                if (cachedValidation.Plan is not null) return new("Ready", cachedValidation.Plan);
            }
        }
        await ledger.HydrateAsync(sessionId, usage, ct).ConfigureAwait(false);
        var estimate = System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(admitted))
            + EllaiModelGuidancePlanner.MaxCompletionTokens + 4096;
        if (!ledger.TryReserve(sessionId, estimate)) return new("BudgetUnavailable");
        var generated = await planner.ComposeAsync(admitted.Objective!, GuidanceMode.Navigation,
            admitted.Catalogue, observed, ct, reportAlreadyReceived).ConfigureAwait(false);
        var sample = new EllaiUsageSample(sessionId, Math.Max(0, generated.Usage.PromptTokens), Math.Max(0, generated.Usage.CompletionTokens));
        ledger.Record(sample);
        await usage.RecordAsync(new(sessionId, sample.PromptTokens, sample.CompletionTokens,
            ledger.DollarsFor(sample), (clock ?? TimeProvider.System).GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        var current = await AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        if (current.Status != "Ready" || current.InteractionEpoch != admitted.InteractionEpoch ||
            current.Observation?.ViewInstanceId != admitted.Observation.ViewInstanceId ||
            current.Observation?.ContextStamp != admitted.Observation.ContextStamp) return new("Stale");
        if (generated.Draft is null) return new("InvalidPlan")
        {
            DiagnosticCode = generated.Usage.FinishReason == "length" ? "output-limit" :
                generated.Errors.Contains("The model returned an invalid guidance payload.") ? "model-json" :
                generated.Errors.Contains("The model changed the admitted guidance objective or mode.") ? "model-objective-mode" : "model-draft"
        };
        if (generated.Draft.Status != GuidancePlanStatus.Ready) return new(generated.Draft.Status.ToString());
        if (reportAlreadyReceived && generated.Draft.Steps.Any(step => step.ControlId == "report.submit"))
            return new("InvalidPlan");
        // Inference may outlive the observation freshness window. Re-admission above
        // fences page/context changes; validate against its newly received state,
        // never retimestamp the snapshot used to prompt the model.
        var currentObserved = discovery.Observe(current.Catalogue!,
            current.Observation!.MountedControlIds.ToHashSet(StringComparer.Ordinal),
            current.Observation.ViewInstanceId, current.Observation.ContextStamp,
            current.Observation.Platform, current.Observation.ReceivedAtUtc, current.Observation.ControlStates);
        var validation = validator.ValidateObserved(generated.Draft, new(feature, [], 20), authority.Features,
            authority.Capabilities, currentObserved, current.Observation.ViewInstanceId, current.Observation.ContextStamp,
            (clock ?? TimeProvider.System).GetUtcNow(), planId);
        if (validation.Plan is not null && cacheKey is not null)
            await EllaiSharedGuidanceTemplates.WriteAsync(templateCache, cacheKey, generated.Draft, current.Catalogue!, ct).ConfigureAwait(false);
        return validation.Plan is null ? new("InvalidPlan")
        {
            DiagnosticCode = validation.Errors.Contains("Guidance requires a fresh, visible and enabled target in the current view.")
                ? "target-state" : validation.Errors.Contains("Guidance metadata is not grounded in its registered control.")
                    ? "registry-metadata" : "plan-contract"
        } : new("Ready", validation.Plan);
    }

    public async Task<EllaiGuidanceProgressDto> ControlAsync(string sessionId,
        EllaiGuidanceControlRequest request, EllaiUiEntitlements authority, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProposalId) || request.ProposalId.Length > 512 ||
            string.IsNullOrWhiteSpace(request.PlanId) || request.PlanId.Length > 120 || request.ExpectedVersion < 1 ||
            request.Command is not ("pause" or "resume" or "cancel" or "back" or "skip")) return new("InvalidRequest");
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != authority.OwnerActorId || session.ScopeEpoch != authority.ScopeEpoch)
            return new("Denied");
        var checkpoint = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
        if (session.Status != "Active" || checkpoint?.Plan?.PlanId != request.PlanId ||
            checkpoint.ProposalId != request.ProposalId || checkpoint.ScopeEpoch != authority.ScopeEpoch ||
            checkpoint.InteractionEpoch > session.InteractionEpoch) return new("Stale");
        var target = request.Command switch { "pause" => "Paused", "resume" => "Ready", _ => "Cancelled" };
        if (request.Command is not ("back" or "skip") && checkpoint.Status == target)
            return new(checkpoint.Status, checkpoint.Version, checkpoint.CurrentStepId, checkpoint.CompletedStepIds, checkpoint.SkippedStepIds);
        if (checkpoint.Version != request.ExpectedVersion)
            return new("Conflict", checkpoint.Version, checkpoint.CurrentStepId, checkpoint.CompletedStepIds, checkpoint.SkippedStepIds);
        if (checkpoint.Status is not ("Ready" or "Paused")) return new("Stale");
        if (request.Command == "skip")
        {
            if (checkpoint.Status != "Ready") return new("Paused", checkpoint.Version, checkpoint.CurrentStepId, checkpoint.CompletedStepIds, checkpoint.SkippedStepIds);
            var admission = await AdmitAsync(sessionId, request.ProposalId, authority, ct).ConfigureAwait(false);
            if (admission.Status != "Ready" || admission.Catalogue is null) return new(admission.Status);
            var step = checkpoint.Plan.Steps.SingleOrDefault(item => item.StepKey == checkpoint.CurrentStepId);
            var descriptor = admission.Catalogue.Descriptors.SingleOrDefault(item => item.ControlId == step?.ControlId);
            // Only optional presentation can be skipped. A model cannot label
            // input, approval, navigation or an application operation complete.
            if (step is null || descriptor is null || step.Behavior != PlanStepBehavior.Explain ||
                descriptor.RiskClass != UiRiskClass.R0 || descriptor.HumanConfirmationRequired ||
                descriptor.ProofPolicyId != "proof-ui-observed" ||
                checkpoint.Plan.RemainingMandatoryObjectiveIds.Contains(step.ObjectiveId) ||
                checkpoint.Plan.Steps.Any(other => other.StepKey != step.StepKey &&
                    admission.Catalogue.Descriptors.Any(item => item.ControlId == other.ControlId && item.PrerequisiteIds.Contains(step.ControlId))))
                return new("SkipNotAllowed", checkpoint.Version, checkpoint.CurrentStepId, checkpoint.CompletedStepIds, checkpoint.SkippedStepIds);
            var skipped = checkpoint with
            {
                Version = checkpoint.Version + 1,
                InteractionEpoch = session.InteractionEpoch,
                CurrentStepId = step.NextStepKey,
                SkippedStepIds = [.. checkpoint.SkippedStepIds, step.StepKey],
                Status = step.NextStepKey is null ? "Completed" : "Ready",
            };
            var updatedSkip = await sessions.TryUpdateAsync(session with
            {
                SessionVersion = session.SessionVersion + 1,
                GuidanceJson = EllaiGuidanceCheckpoint.Serialize(skipped),
                UpdatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow(),
            }, session.SessionVersion, ct).ConfigureAwait(false);
            return updatedSkip is null ? new("Conflict") : new(skipped.Status, skipped.Version, skipped.CurrentStepId, skipped.CompletedStepIds, skipped.SkippedStepIds);
        }
        if (request.Command == "back")
        {
            if (checkpoint.Status != "Ready") return new("Paused", checkpoint.Version, checkpoint.CurrentStepId, checkpoint.CompletedStepIds, checkpoint.SkippedStepIds);
            var admission = await AdmitAsync(sessionId, request.ProposalId, authority, ct).ConfigureAwait(false);
            if (admission.Status != "Ready") return new(admission.Status);
            var ordered = EllaiGuidanceSequence.Order(checkpoint.Plan.Steps).Select(step => step.StepKey).ToArray();
            var index = Array.IndexOf(ordered, checkpoint.CurrentStepId);
            if (index < 0 || !checkpoint.CompletedStepIds.Concat(checkpoint.SkippedStepIds).ToHashSet(StringComparer.Ordinal).SetEquals(ordered.Take(index))) return new("Stale");
            if (index == 0) return new("AtStart", checkpoint.Version, checkpoint.CurrentStepId, checkpoint.CompletedStepIds, checkpoint.SkippedStepIds);
            // Revisit presentation only. Discard later progress so continuing must
            // prove completion again; no application action or receipt is replayed.
            var previous = checkpoint with
            {
                Version = checkpoint.Version + 1,
                InteractionEpoch = session.InteractionEpoch,
                CurrentStepId = ordered[index - 1],
                CompletedStepIds = checkpoint.CompletedStepIds.Where(id => ordered.Take(index - 1).Contains(id)).ToArray(),
                SkippedStepIds = checkpoint.SkippedStepIds.Where(id => ordered.Take(index - 1).Contains(id)).ToArray(),
            };
            var rewound = await sessions.TryUpdateAsync(session with
            {
                SessionVersion = session.SessionVersion + 1,
                GuidanceJson = EllaiGuidanceCheckpoint.Serialize(previous),
                UpdatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow(),
            }, session.SessionVersion, ct).ConfigureAwait(false);
            return rewound is null ? new("Conflict") : new(previous.Status, previous.Version, previous.CurrentStepId, previous.CompletedStepIds, previous.SkippedStepIds);
        }
        if (request.Command == "resume")
        {
            var admission = await AdmitAsync(sessionId, request.ProposalId, authority, ct).ConfigureAwait(false);
            if (admission.Status != "Ready" || admission.Observation?.ContextStamp != checkpoint.ContextStamp)
                return new("NeedsObservation");
        }
        var next = checkpoint with { Status = target, Version = checkpoint.Version + 1, InteractionEpoch = session.InteractionEpoch };
        var updated = await sessions.TryUpdateAsync(session with
        {
            SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(next),
            UpdatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow(),
        }, session.SessionVersion, ct).ConfigureAwait(false);
        return updated is null ? new("Conflict") : new(next.Status, next.Version, next.CurrentStepId, next.CompletedStepIds, next.SkippedStepIds);
    }

    /// <summary>Commits only evidence-supported progress in the existing session transaction.
    /// Replay never executes an action; client claims cannot prove edits or intake submission.</summary>
    public async Task<EllaiGuidanceProgressDto> CompleteStepAsync(string sessionId,
        EllaiGuidanceStepRequest request, EllaiUiEntitlements authority, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProposalId) || request.ProposalId.Length > 512 ||
            string.IsNullOrWhiteSpace(request.PlanId) || request.PlanId.Length > 120 || request.ExpectedVersion < 1 ||
            string.IsNullOrWhiteSpace(request.StepId) || request.StepId.Length > 120 ||
            string.IsNullOrWhiteSpace(request.ContextStamp) || request.ContextStamp.Length > 512)
            return new("InvalidRequest");
        var admitted = await AdmitAsync(sessionId, request.ProposalId, authority, ct).ConfigureAwait(false);
        if (admitted.Status != "Ready" || admitted.Observation is null || admitted.Catalogue is null)
            return new(admitted.Status);
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != "Active" || session.OwnerActorId != authority.OwnerActorId ||
            session.ScopeEpoch != authority.ScopeEpoch || session.InteractionEpoch != admitted.InteractionEpoch)
            return new("Stale");
        var saved = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
        var step = saved?.Plan?.Steps.SingleOrDefault(item => item.StepKey == request.StepId);
        var navigation = step is null ? null : EllaiNavigationManifest.ResolveStepDestination(step);
        var descriptor = admitted.Catalogue.Descriptors.SingleOrDefault(item => item.ControlId == step?.ControlId);
        var inputStates = admitted.Observation.ControlStates?.Where(state => state.ControlId == step?.ControlId).ToArray() ?? [];
        // A wizard may unmount an accepted input when moving to its next page. A
        // still-mounted hidden/disabled/ambiguous input is not equivalent evidence.
        var consistentInputState = inputStates.Length == 0
            ? step is not null && !admitted.Observation.MountedControlIds.Contains(step.ControlId)
            : inputStates.Length == 1 && inputStates[0].State is UiObservationState.Ready or UiObservationState.Unmounted;
        // This is presentation progress for the real report checkbox only. It
        // grants no tool approval and does not establish report persistence.
        var formConfirmation = step?.Behavior == PlanStepBehavior.GuideHuman &&
            descriptor is { ControlId: "report.confirmations", FeatureId: EllaiReportUiManifest.FeatureId,
                ProofPolicyId: "proof-human-acknowledged", HumanConfirmationRequired: true, RiskClass: UiRiskClass.R2 } &&
            inputStates.Length == 1 && inputStates[0].State == UiObservationState.Ready &&
            admitted.Observation.MountedControlIds.Contains("report.confirmations") &&
            admitted.Observation.CompletedControlIds?.Contains("report.confirmations") == true;
        var validatedInput = formConfirmation || step?.Behavior == PlanStepBehavior.GuideHuman && descriptor?.ProofPolicyId == "proof-ui-validated" &&
            !descriptor.HumanConfirmationRequired && consistentInputState &&
            admitted.Observation.CompletedControlIds?.Contains(step.ControlId) == true;
        if (saved?.Plan is not { } plan || saved.ProposalId != request.ProposalId || plan.PlanId != request.PlanId ||
            saved.ScopeEpoch != authority.ScopeEpoch || saved.InteractionEpoch > admitted.InteractionEpoch ||
            (navigation is null && !validatedInput && saved.ContextStamp != request.ContextStamp) || admitted.Observation.ContextStamp != request.ContextStamp ||
            saved.Status is not ("Ready" or "Paused" or "Completed")) return new("Stale");
        if (saved.CompletedStepIds.Contains(request.StepId))
            return new(saved.Status, saved.Version, saved.CurrentStepId, saved.CompletedStepIds, saved.SkippedStepIds);
        if (saved.Version != request.ExpectedVersion || saved.CurrentStepId != request.StepId || saved.Status != "Ready")
            return new("Conflict", saved.Version, saved.CurrentStepId, saved.CompletedStepIds, saved.SkippedStepIds);
        if (step is null || descriptor is null || (step.Behavior != PlanStepBehavior.Explain && navigation is null && !validatedInput) ||
            (navigation is not null && (descriptor.FeatureId != EllaiNavigationManifest.FeatureId ||
                admitted.Observation.CurrentNavigationId != navigation.NavigationId)) ||
            (descriptor.HumanConfirmationRequired && !formConfirmation) ||
            (navigation is null && !validatedInput && descriptor.ProofPolicyId != "proof-ui-observed") ||
            (!validatedInput && (!admitted.Observation.MountedControlIds.Contains(step.ControlId) ||
                admitted.Observation.ControlStates?.Count(item => item.ControlId == step.ControlId && item.State == UiObservationState.Ready) != 1)))
            return new("EvidenceRequired", saved.Version, saved.CurrentStepId, saved.CompletedStepIds, saved.SkippedStepIds);
        var nextStep = step.NextStepKey;
        var next = saved with
        {
            Version = saved.Version + 1,
            InteractionEpoch = admitted.InteractionEpoch,
            CurrentStepId = nextStep,
            ContextStamp = request.ContextStamp,
            NavigationId = navigation?.NavigationId ?? saved.NavigationId,
            CompletedStepIds = [.. saved.CompletedStepIds, step.StepKey],
            Status = nextStep is null ? "Completed" : "Ready",
        };
        var updated = await sessions.TryUpdateAsync(session with
        {
            SessionVersion = session.SessionVersion + 1,
            GuidanceJson = EllaiGuidanceCheckpoint.Serialize(next),
            UpdatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow(),
        }, session.SessionVersion, ct).ConfigureAwait(false);
        return updated is null ? new("Conflict") : new(next.Status, next.Version, next.CurrentStepId, next.CompletedStepIds, next.SkippedStepIds);
    }

    public async Task<EllaiGuidanceAdmission> AdmitAsync(string sessionId, string proposalId,
        EllaiUiEntitlements authority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (string.IsNullOrWhiteSpace(authority.OwnerActorId) || string.IsNullOrWhiteSpace(authority.ScopeEpoch))
            return new("Denied");
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != authority.OwnerActorId ||
            session.ScopeEpoch != authority.ScopeEpoch || session.Status != "Active") return new("Denied");
        var turns = await outbox.ReadConversationAsync(sessionId, authority.OwnerActorId, ct).ConfigureAwait(false);
        var latest = turns.MaxBy(turn => turn.Sequence);
        if (latest is null || latest.Outcome != "Completed") return new("Stale");
        var latestProposals = EllaiConversationProjection.ToClient(latest).UiProposals;
        var proposal = latestProposals.SingleOrDefault(item => item.ProposalId == proposalId)?.Proposal;
        if (proposal is null)
        {
            // An accepted guidance plan belongs to the conversation, not only to
            // its originating turn. Only a new guidance proposal supersedes it.
            var checkpoint = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
            if (checkpoint?.Plan is null || checkpoint.ProposalId != proposalId ||
                checkpoint.ScopeEpoch != authority.ScopeEpoch || checkpoint.InteractionEpoch > session.InteractionEpoch ||
                latestProposals.Any(item => item.Proposal.Kind == UiIntentKind.StartGuidance)) return new("Stale");
            var origin = await outbox.ReadLatestGuidanceTurnAsync(sessionId, authority.OwnerActorId, ct).ConfigureAwait(false);
            // This query deliberately spans durable history: a superseding proposal must
            // still invalidate the old plan after both proposals leave the recent window.
            proposal = origin is null ? null : EllaiConversationProjection.ToClient(origin).UiProposals
                .SingleOrDefault(item => item.ProposalId == proposalId)?.Proposal;
        }
        if (proposal is null) return new("Stale");
        if (proposal.Kind != UiIntentKind.StartGuidance || proposal.FeatureId != "ellai.guidance" ||
            proposal.ResourceId is not null || proposal.ControlId is not null ||
            !proposal.Arguments.TryGetValue("feature", out var feature) ||
            !proposal.Arguments.TryGetValue("objective", out var objective) || string.IsNullOrWhiteSpace(objective) ||
            proposal.Arguments.Keys.Any(key => key is not ("feature" or "objective"))) return new("InvalidProposal");
        if (!authority.Features.Contains(feature)) return new("Denied");
        var catalogue = registry.GetSnapshot(new(feature, [], 20), authority.Features, authority.Capabilities);
        if (catalogue.Descriptors.Count == 0) return new("Unavailable");
        var observation = await observations.LatestAsync(sessionId, ct).ConfigureAwait(false);
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current is null || current.Status != "Active" || current.OwnerActorId != session.OwnerActorId ||
            current.ScopeEpoch != session.ScopeEpoch || current.InteractionEpoch != session.InteractionEpoch ||
            current.SessionVersion != session.SessionVersion) return new("Stale");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (observation is null || observation.ReceivedAtUtc > now || now - observation.ReceivedAtUtc > TimeSpan.FromSeconds(45))
            return new("NeedsObservation", objective, catalogue, InteractionEpoch: session.InteractionEpoch);
        var hasTarget = catalogue.Descriptors.Any(descriptor => observation.MountedControlIds.Contains(descriptor.ControlId));
        var actionable = catalogue.Descriptors.Any(descriptor => observation.MountedControlIds.Contains(descriptor.ControlId) &&
            observation.ControlStates?.Count(state => state.ControlId == descriptor.ControlId && state.State == UiObservationState.Ready) == 1);
        return new(!hasTarget ? "NeedsNavigation" : actionable ? "Ready" : "WaitingForControl",
            objective, catalogue, observation, session.InteractionEpoch);
    }
}

public sealed record EllaiGuidancePlanningResult(string Status, ValidatedGuidancePlan? Plan = null)
{
    // Fixed application codes only. Never copy model text, field values or
    // validation messages into operational logs or client-facing guidance.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DiagnosticCode { get; init; }
}
