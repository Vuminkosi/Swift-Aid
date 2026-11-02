using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Server-resolved grants. Never bind this record from an HTTP request or model output.</summary>
public sealed record EllaiUiEntitlements(string OwnerActorId, string ScopeEpoch,
    IReadOnlySet<string> Features, IReadOnlySet<string> Capabilities);

public sealed record EllaiGuidanceAdmission(string Status, string? Objective = null,
    UiRegistrySnapshot? Catalogue = null, StoredUiObservationReport? Observation = null,
    long InteractionEpoch = 0);

/// <summary>Resolves a durable proposal and fences it against session ownership, scope,
/// newer turns and current UI evidence before the existing model planner is invoked.</summary>
public sealed class EllaiGuidanceProposalAdmission(
    IEllaiSessionStore sessions, IEllaiUtteranceOutboxStore outbox,
    EllaiUiRegistry registry, IEllaiUiObservationStore observations, TimeProvider? clock = null)
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
            && previous.InteractionEpoch == admitted.InteractionEpoch;
        var sameContext = sameProposal && previous!.ContextStamp == admitted.Observation.ContextStamp;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (sameContext && previous!.Status == "Generating" && previous.LeaseExpiresAtUtc > now)
            return new("Generating");
        if (sameContext && previous!.Status is "Cancelled" or "Completed")
            return new(previous.Status);
        if (sameContext && previous!.Status is "Ready" or "Paused" && previous.Plan is { } existing)
        {
            var observed = discovery.Observe(admitted.Catalogue,
                admitted.Observation.MountedControlIds.ToHashSet(StringComparer.Ordinal),
                admitted.Observation.ViewInstanceId, admitted.Observation.ContextStamp,
                admitted.Observation.Platform, admitted.Observation.ReceivedAtUtc, admitted.Observation.ControlStates);
            var draft = new EllaiGuidancePlanDraft(1, GuidancePlanStatus.Ready, admitted.Objective!,
                GuidanceMode.Navigation, existing.Steps, [], null);
            var accepted = validator.ValidateObserved(draft, new(admitted.Catalogue.Descriptors[0].FeatureId, [], 20),
                authority.Features, authority.Capabilities, observed, admitted.Observation.ViewInstanceId,
                admitted.Observation.ContextStamp, now, existing.PlanId);
            if (accepted.Plan is not null && existing.CatalogueVersion == admitted.Catalogue.CatalogueVersion
                && existing.UiContractVersion == admitted.Catalogue.UiContractVersion)
                return new(previous.Status, existing);
        }
        var attempt = sameProposal ? previous!.GenerationAttempt + 1 : 1;
        if (attempt > EllaiGuidancePlanValidator.MaxReplanDispatches) return new("ReplanLimit");
        var checkpoint = new EllaiGuidanceCheckpoint(proposalId, admitted.Observation.ContextStamp,
            authority.ScopeEpoch, admitted.InteractionEpoch, (previous?.Version ?? 0) + 1,
            Guid.NewGuid().ToString("N"), "Generating", now.AddMinutes(2), null, null, [], [],
            admitted.Catalogue.Descriptors[0].NavigationId) { GenerationAttempt = attempt };
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
        await ledger.HydrateAsync(sessionId, usage, ct).ConfigureAwait(false);
        var estimate = System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(admitted)) + 4096;
        if (!ledger.TryReserve(sessionId, estimate)) return new("BudgetUnavailable");
        var observed = discovery.Observe(admitted.Catalogue,
            admitted.Observation.MountedControlIds.ToHashSet(StringComparer.Ordinal),
            admitted.Observation.ViewInstanceId, admitted.Observation.ContextStamp,
            admitted.Observation.Platform, admitted.Observation.ReceivedAtUtc, admitted.Observation.ControlStates);
        var generated = await planner.ComposeAsync(admitted.Objective!, GuidanceMode.Navigation,
            admitted.Catalogue, observed, ct).ConfigureAwait(false);
        var sample = new EllaiUsageSample(sessionId, Math.Max(0, generated.Usage.PromptTokens), Math.Max(0, generated.Usage.CompletionTokens));
        ledger.Record(sample);
        await usage.RecordAsync(new(sessionId, sample.PromptTokens, sample.CompletionTokens,
            ledger.DollarsFor(sample), (clock ?? TimeProvider.System).GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        var current = await AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        if (current.Status != "Ready" || current.InteractionEpoch != admitted.InteractionEpoch ||
            current.Observation?.ViewInstanceId != admitted.Observation.ViewInstanceId ||
            current.Observation?.ContextStamp != admitted.Observation.ContextStamp) return new("Stale");
        if (generated.Draft is null) return new("InvalidPlan");
        if (generated.Draft.Status != GuidancePlanStatus.Ready) return new(generated.Draft.Status.ToString());
        var feature = admitted.Catalogue.Descriptors[0].FeatureId;
        var planId = "plan-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] { sessionId, proposalId, admitted.Observation.ContextStamp })))).ToLowerInvariant();
        var validation = validator.ValidateObserved(generated.Draft, new(feature, [], 20), authority.Features,
            authority.Capabilities, observed, admitted.Observation.ViewInstanceId, admitted.Observation.ContextStamp,
            (clock ?? TimeProvider.System).GetUtcNow(), planId);
        return validation.Plan is null ? new("InvalidPlan") : new("Ready", validation.Plan);
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
        var proposal = EllaiConversationProjection.ToClient(latest).UiProposals
            .SingleOrDefault(item => item.ProposalId == proposalId)?.Proposal;
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

public sealed record EllaiGuidancePlanningResult(string Status, ValidatedGuidancePlan? Plan = null);
