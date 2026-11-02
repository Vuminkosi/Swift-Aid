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
    /// <summary>Generates through the existing planner, charges usage, then rechecks the durable
    /// proposal and current UI context. The result still requires durable activation.</summary>
    public async Task<EllaiGuidancePlanningResult> PlanAsync(string sessionId, string proposalId,
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
